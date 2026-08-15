#!/usr/bin/env python3
"""Install and smoke-test the exact external tools supported by FsLangMCP.

The source of truth is dotnet-tools.json.  ``install`` materializes the pinned
tools in an isolated directory (never globally); ``smoke`` verifies the runtime
tool set and exercises FsLangMCP's real MCP -> FSAC/proj-info integration.
"""

from __future__ import annotations

import argparse
from collections import deque
import json
import os
from pathlib import Path
import queue
import re
import signal
import shutil
import subprocess
import sys
import threading
import time
from typing import NoReturn


REPOSITORY_ROOT = Path(__file__).resolve().parent.parent
DEFAULT_MANIFEST = REPOSITORY_ROOT / "dotnet-tools.json"
SEMVER = re.compile(
    r"^(0|[1-9][0-9]*)\."
    r"(0|[1-9][0-9]*)\."
    r"(0|[1-9][0-9]*)"
    r"(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$"
)
RUNTIME_TOOLS = {
    "fantomas": "fantomas",
    "fsautocomplete": "fsautocomplete",
    "ionide.projinfo.tool": "proj-info",
}


def fail(message: str) -> NoReturn:
    raise RuntimeError(message)


def resolve_dotnet(value: str | None) -> str:
    dotnet = value or os.environ.get("DOTNET_HOST_PATH") or shutil.which("dotnet")
    if not dotnet:
        fail("dotnet was not found; pass --dotnet or put the pinned SDK on PATH")
    return str(Path(dotnet).resolve())


def dotnet_environment(dotnet: str) -> dict[str, str]:
    env = os.environ.copy()
    dotnet_root = str(Path(dotnet).parent)
    env["DOTNET_ROOT"] = dotnet_root
    env["PATH"] = os.pathsep.join([dotnet_root, env.get("PATH", "")])
    return env


def load_tool_versions(manifest_path: Path) -> dict[str, tuple[str, str]]:
    manifest_path = manifest_path.resolve()
    try:
        manifest = json.loads(manifest_path.read_text(encoding="utf-8-sig"))
    except (OSError, json.JSONDecodeError) as error:
        fail(f"cannot read tool manifest {manifest_path}: {error}")

    if manifest.get("version") != 1 or manifest.get("isRoot") is not True:
        fail(f"{manifest_path} must be a root dotnet tool manifest with version 1")

    tools = manifest.get("tools")
    if not isinstance(tools, dict):
        fail(f"{manifest_path} has no tools object")

    result: dict[str, tuple[str, str]] = {}
    for package_id, expected_command in RUNTIME_TOOLS.items():
        entry = tools.get(package_id)
        if not isinstance(entry, dict):
            fail(f"{manifest_path} does not pin {package_id}")

        version = entry.get("version")
        commands = entry.get("commands")
        if not isinstance(version, str) or not SEMVER.fullmatch(version):
            fail(f"{package_id} version must be one exact SemVer, got {version!r}")
        if entry.get("rollForward") is not False:
            fail(f"{package_id} must set rollForward=false")
        if commands != [expected_command]:
            fail(
                f"{package_id} must expose only {expected_command!r}, got {commands!r}"
            )

        result[package_id] = (version, expected_command)

    return result


def tool_executable(tool_path: Path, command: str) -> Path:
    candidates = (
        [tool_path / f"{command}.exe", tool_path / command]
        if os.name == "nt"
        else [tool_path / command, tool_path / f"{command}.exe"]
    )
    for candidate in candidates:
        if candidate.is_file():
            return candidate.resolve()
    fail(f"tool command {command!r} was not created under {tool_path}")


def run_checked(
    command: list[str],
    *,
    cwd: Path | None = None,
    env: dict[str, str] | None = None,
    timeout: float = 300.0,
) -> subprocess.CompletedProcess[str]:
    try:
        result = subprocess.run(
            command,
            cwd=cwd,
            env=env,
            text=True,
            encoding="utf-8",
            capture_output=True,
            timeout=timeout,
        )
    except subprocess.TimeoutExpired:
        fail(f"command timed out after {timeout:g}s: {command!r}")

    if result.returncode != 0:
        fail(
            f"command failed with exit code {result.returncode}: {command!r}\n"
            f"stdout:\n{result.stdout}\nstderr:\n{result.stderr}"
        )
    return result


def verify_installed_tools(
    tool_path: Path,
    versions: dict[str, tuple[str, str]],
    env: dict[str, str],
    manifest_path: Path,
) -> dict[str, Path]:
    env = env.copy()
    env["PATH"] = os.pathsep.join([str(tool_path), env.get("PATH", "")])

    for package_id, (version, _) in versions.items():
        store_directory = tool_path / ".store" / package_id.lower() / version
        if not store_directory.is_dir():
            fail(
                f"tool store is missing exact {package_id} {version}: "
                f"{store_directory}"
            )

    executables = {
        package_id: tool_executable(tool_path, command)
        for package_id, (_, command) in versions.items()
    }

    fsac_version = versions["fsautocomplete"][0]
    fsac_result = run_checked(
        [str(executables["fsautocomplete"]), "--version"], env=env, timeout=30
    )
    actual_fsac_version = fsac_result.stdout.strip().split("+", 1)[0]
    if actual_fsac_version != fsac_version:
        fail(
            f"fsautocomplete version mismatch: expected {fsac_version}, "
            f"got {fsac_result.stdout.strip()!r}"
        )

    fantomas_version = versions["fantomas"][0]
    fantomas_result = run_checked(
        [str(executables["fantomas"]), "--version"], env=env, timeout=30
    )
    actual_fantomas_version = (
        fantomas_result.stdout.strip().removeprefix("Fantomas v").split("+", 1)[0]
    )
    if actual_fantomas_version != fantomas_version:
        fail(
            f"fantomas version mismatch: expected {fantomas_version}, "
            f"got {fantomas_result.stdout.strip()!r}"
        )

    dotnet = shutil.which("dotnet", path=env.get("PATH"))
    if not dotnet:
        fail("dotnet was not found while verifying the Fantomas daemon command")
    dotnet_fantomas_result = run_checked(
        [dotnet, "fantomas", "--version"],
        cwd=manifest_path.resolve().parent,
        env=env,
        timeout=30,
    )
    actual_dotnet_fantomas_version = (
        dotnet_fantomas_result.stdout.strip()
        .removeprefix("Fantomas v")
        .split("+", 1)[0]
    )
    if actual_dotnet_fantomas_version != fantomas_version:
        fail(
            f"dotnet fantomas version mismatch: expected {fantomas_version}, "
            f"got {dotnet_fantomas_result.stdout.strip()!r}"
        )

    return executables


def install_tools(args: argparse.Namespace) -> None:
    manifest = Path(args.manifest).resolve()
    tool_path = Path(args.tool_path).resolve()
    dotnet = resolve_dotnet(args.dotnet)
    env = dotnet_environment(dotnet)
    versions = load_tool_versions(manifest)
    tool_path.mkdir(parents=True, exist_ok=True)

    for package_id, (version, command) in versions.items():
        already_installed = any(
            candidate.is_file()
            for candidate in (tool_path / command, tool_path / f"{command}.exe")
        )
        verb = "update" if already_installed else "install"
        run_checked(
            [
                dotnet,
                "tool",
                verb,
                package_id,
                "--tool-path",
                str(tool_path),
                "--version",
                version,
                "--allow-downgrade",
                "--no-http-cache",
            ],
            env=env,
            timeout=args.timeout_seconds,
        )

        store = tool_path / ".store" / package_id.lower() / version
        if not store.is_dir():
            fail(f"dotnet installed {package_id}, but exact store {store} is absent")
        print(f"installed {package_id} {version}")

    run_checked(
        [dotnet, "tool", "restore", "--tool-manifest", str(manifest)],
        cwd=manifest.parent,
        env=env,
        timeout=args.timeout_seconds,
    )

    verify_installed_tools(tool_path, versions, env, manifest)
    print(f"runtime toolchain ready: {tool_path}")


class JsonLineProcess:
    def __init__(
        self, command: list[str], *, cwd: Path, env: dict[str, str]
    ) -> None:
        self.process = subprocess.Popen(
            command,
            cwd=cwd,
            env=env,
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            encoding="utf-8",
            bufsize=1,
        )
        if (
            self.process.stdin is None
            or self.process.stdout is None
            or self.process.stderr is None
        ):
            fail("failed to open FsLangMCP stdio")

        self.responses: "queue.Queue[str | None]" = queue.Queue()
        self.stderr: deque[str] = deque(maxlen=200)

        def read_stdout() -> None:
            try:
                for line in self.process.stdout:
                    self.responses.put(line)
            finally:
                self.responses.put(None)

        def read_stderr() -> None:
            self.stderr.extend(self.process.stderr)

        self.stdout_thread = threading.Thread(target=read_stdout, daemon=True)
        self.stderr_thread = threading.Thread(target=read_stderr, daemon=True)
        self.stdout_thread.start()
        self.stderr_thread.start()

    def stderr_tail(self) -> str:
        return "".join(self.stderr).strip()

    def send(self, message: dict) -> None:
        assert self.process.stdin is not None
        self.process.stdin.write(json.dumps(message, separators=(",", ":")) + "\n")
        self.process.stdin.flush()

    def receive(self, expected_id: int, timeout: float) -> dict:
        deadline = time.monotonic() + timeout
        seen: list[str] = []
        while True:
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                fail(
                    f"timed out waiting for MCP response id={expected_id}; "
                    f"stdout={seen!r}; stderr={self.stderr_tail()!r}"
                )
            try:
                line = self.responses.get(timeout=remaining)
            except queue.Empty:
                continue
            if line is None:
                fail(
                    f"FsLangMCP closed stdout before response id={expected_id}; "
                    f"stderr={self.stderr_tail()!r}"
                )

            stripped = line.strip()
            if not stripped:
                continue
            seen.append(stripped)
            try:
                message = json.loads(stripped)
            except json.JSONDecodeError:
                continue
            if isinstance(message, dict) and message.get("id") == expected_id:
                return message

    def close(self) -> None:
        assert self.process.stdin is not None
        try:
            self.process.stdin.close()
        except OSError:
            pass
        try:
            self.process.wait(timeout=20)
        except subprocess.TimeoutExpired:
            self.process.kill()
            self.process.wait(timeout=10)
        self.stdout_thread.join(timeout=2)
        self.stderr_thread.join(timeout=2)


def tool_payload(response: dict, tool_name: str) -> object:
    if "error" in response:
        fail(f"{tool_name} MCP request failed: {response['error']}")
    result = response.get("result")
    if not isinstance(result, dict) or result.get("isError") is True:
        fail(f"{tool_name} returned an MCP tool error: {response}")
    content = result.get("content")
    if not isinstance(content, list):
        fail(f"{tool_name} returned no content: {response}")
    text_blocks = [
        item.get("text")
        for item in content
        if isinstance(item, dict) and item.get("type") == "text"
    ]
    if len(text_blocks) != 1 or not isinstance(text_blocks[0], str):
        fail(f"{tool_name} returned unexpected content: {response}")
    try:
        return json.loads(text_blocks[0])
    except json.JSONDecodeError as error:
        fail(f"{tool_name} returned invalid JSON text: {error}")


def call_tool(
    mcp: JsonLineProcess,
    request_id: int,
    tool_name: str,
    arguments: dict,
    timeout: float,
) -> object:
    mcp.send(
        {
            "jsonrpc": "2.0",
            "id": request_id,
            "method": "tools/call",
            "params": {"name": tool_name, "arguments": arguments},
        }
    )
    return tool_payload(mcp.receive(request_id, timeout), tool_name)


def contains_new_text(value: object, expected: str) -> bool:
    if isinstance(value, dict):
        if value.get("newText") == expected:
            return True
        return any(contains_new_text(item, expected) for item in value.values())
    if isinstance(value, list):
        return any(contains_new_text(item, expected) for item in value)
    return False


def child_pid(runtime_status: object) -> int | None:
    if not isinstance(runtime_status, dict) or runtime_status.get("status") != "ok":
        fail(f"fsharp_runtime_status returned an invalid payload: {runtime_status!r}")
    children = runtime_status.get("children")
    if not isinstance(children, list):
        fail(f"fsharp_runtime_status omitted children: {runtime_status!r}")
    if not children:
        return None
    first = children[0]
    pid = first.get("pid") if isinstance(first, dict) else None
    if not isinstance(pid, int) or pid <= 0:
        fail(f"fsharp_runtime_status returned an invalid FSAC pid: {first!r}")
    return pid


def paths_equal(left: str, right: Path) -> bool:
    return os.path.normcase(os.path.realpath(left)) == os.path.normcase(
        os.path.realpath(right)
    )


def require_contextual_response(
    response: object, expected_project: Path, operation: str
) -> int:
    if not isinstance(response, dict) or response.get("status") != "ok":
        fail(f"{operation} did not succeed in the CLI project context: {response!r}")
    if response.get("contextMatched") is not True:
        fail(f"{operation} did not confirm its FSAC context: {response!r}")

    active_project = response.get("activeProjectPath")
    if not isinstance(active_project, str) or not paths_equal(
        active_project, expected_project
    ):
        fail(
            f"{operation} used the wrong active project: "
            f"expected {expected_project}, got {active_project!r}"
        )

    generation = response.get("sessionGeneration")
    if type(generation) is not int or generation <= 0:
        fail(f"{operation} omitted a live FSAC session generation: {response!r}")
    return generation


def require_single_path(values: object, expected: Path, field: str) -> None:
    if (
        not isinstance(values, list)
        or len(values) != 1
        or not isinstance(values[0], str)
        or not paths_equal(values[0], expected)
    ):
        fail(f"{field} is not exactly the expected source file: {values!r}")


def require_fsac_rebase(
    response: object,
    *,
    expected_project: Path,
    expected_glob: str,
    previous_generation: int,
) -> int:
    if not isinstance(response, dict):
        fail(f"diagnostics rebase returned a non-object payload: {response!r}")

    exact_fields = {
        "status": "succeeded",
        "verdict": "unknown",
        "scope": "workspace",
        "speed": "fast",
        "via": "fsac",
        "fileGlob": expected_glob,
    }
    mismatches = {
        field: response.get(field)
        for field, expected in exact_fields.items()
        if response.get(field) != expected
    }
    if mismatches:
        fail(
            "diagnostics rebase contract mismatch: "
            f"{mismatches!r}; payload={response!r}"
        )
    if response.get("contextMatched") is not True:
        fail(f"diagnostics rebase escaped the requested context: {response!r}")
    if response.get("expectationComplete") is not True:
        fail(f"diagnostics rebase lacked evaluated SourceFiles: {response!r}")

    requested_project = response.get("requestedProjectPath")
    if not isinstance(requested_project, str) or not paths_equal(
        requested_project, expected_project
    ):
        fail(
            "diagnostics rebase used the wrong project: "
            f"expected {expected_project}, got {requested_project!r}"
        )

    generation = response.get("sessionGeneration")
    if type(generation) is not int or generation <= previous_generation:
        fail(
            "the first context-bound diagnostics request did not rebase FSAC onto "
            f"the evaluated inputs: before={previous_generation}, after={generation!r}"
        )
    return generation


def require_fsac_diagnostics(
    response: object,
    *,
    expected_project: Path,
    expected_file: Path,
    expected_glob: str,
    expected_generation: int,
) -> None:
    if not isinstance(response, dict):
        fail(f"fast diagnostics returned a non-object payload: {response!r}")

    exact_fields = {
        "status": "succeeded",
        "verdict": "errors",
        "scope": "workspace",
        "speed": "fast",
        "via": "fsac",
        "fileGlob": expected_glob,
    }
    mismatches = {
        field: response.get(field)
        for field, expected in exact_fields.items()
        if response.get(field) != expected
    }
    if mismatches:
        fail(f"fast diagnostics contract mismatch: {mismatches!r}; payload={response!r}")

    if response.get("contextMatched") is not True:
        fail(f"fast diagnostics were not bound to the active FSAC context: {response!r}")
    if response.get("complete") is not True or response.get("analyzed") is not True:
        fail(f"FSAC diagnostics coverage is incomplete: {response!r}")

    requested_project = response.get("requestedProjectPath")
    if not isinstance(requested_project, str) or not paths_equal(
        requested_project, expected_project
    ):
        fail(
            "fast diagnostics used the wrong requested project: "
            f"expected {expected_project}, got {requested_project!r}"
        )

    generation = response.get("sessionGeneration")
    if type(generation) is not int or generation != expected_generation:
        fail(
            "fast diagnostics escaped the active FSAC generation: "
            f"expected {expected_generation}, got {generation!r}"
        )

    count_fields = {
        "expectedFileCount": 1,
        "receivedFileCount": 1,
        "missingFileCount": 0,
        "staleFileCount": 0,
        "analyzedFileCount": 1,
    }
    bad_counts = {
        field: response.get(field)
        for field, expected in count_fields.items()
        if type(response.get(field)) is not int or response.get(field) != expected
    }
    if bad_counts:
        fail(f"fast diagnostics coverage counts are invalid: {bad_counts!r}")

    require_single_path(response.get("expectedFiles"), expected_file, "expectedFiles")
    require_single_path(response.get("receivedFiles"), expected_file, "receivedFiles")
    if response.get("missingFiles") != [] or response.get("staleFiles") != []:
        fail(f"fast diagnostics reported missing or stale files: {response!r}")

    analyzed_at = response.get("mostRecentAnalyzedAt")
    if not isinstance(analyzed_at, str) or not analyzed_at.strip():
        fail(f"fast diagnostics omitted the FSAC publication timestamp: {response!r}")

    diagnostics = response.get("diagnostics")
    if not isinstance(diagnostics, list) or not any(
        isinstance(diagnostic, dict) and diagnostic.get("severity") == 1
        for diagnostic in diagnostics
    ):
        fail(f"FSAC publishDiagnostics did not surface the deliberate error: {response!r}")


def smoke_tools(args: argparse.Namespace) -> None:
    manifest = Path(args.manifest)
    tool_path = Path(args.tool_path).resolve()
    server = Path(args.server).resolve()
    project = Path(args.project).resolve()
    dotnet = resolve_dotnet(args.dotnet)
    child_env = dotnet_environment(dotnet)
    versions = load_tool_versions(manifest)
    executables = verify_installed_tools(tool_path, versions, child_env, manifest)

    if not server.is_file():
        fail(f"FsLangMCP server does not exist: {server}")
    if not project.is_file():
        fail(f"smoke project does not exist: {project}")

    child_env["PATH"] = os.pathsep.join(
        [str(tool_path), str(Path(dotnet).parent), child_env.get("PATH", "")]
    )
    child_env["FSAC_COMMAND"] = str(executables["fsautocomplete"])
    timeout_ms = str(int(args.timeout_seconds * 1000))
    child_env["FSLANGMCP_LSP_STARTUP_TIMEOUT_MS"] = timeout_ms
    child_env["FSLANGMCP_LSP_REQUEST_TIMEOUT_MS"] = timeout_ms
    child_env["FSLANGMCP_PROJ_INFO_TIMEOUT_MS"] = timeout_ms

    proj_info = run_checked(
        [
            str(executables["ionide.projinfo.tool"]),
            "--project",
            str(project),
            "--fcs",
            "--serialize",
        ],
        cwd=project.parent,
        env=child_env,
        timeout=args.timeout_seconds,
    )
    try:
        proj_info_payload = json.loads(proj_info.stdout.lstrip("\ufeff"))
    except json.JSONDecodeError as error:
        fail(f"proj-info returned invalid JSON: {error}")
    if not isinstance(proj_info_payload, list) or not proj_info_payload:
        fail(f"proj-info returned no project options: {proj_info_payload!r}")
    first_project = proj_info_payload[0]
    if not isinstance(first_project, dict) or not paths_equal(
        str(first_project.get("ProjectFileName", "")), project
    ):
        fail(f"proj-info evaluated the wrong project: {first_project!r}")

    smoke_file = (project.parent / "Version.fs").resolve()
    if not smoke_file.is_file():
        fail(f"live smoke source does not exist: {smoke_file}")

    broken_source = """module FsLangMcp.Version

let current: string = 42
let productName: string = "FsLangMCP"
"""
    tainted_source = broken_source.replace("= 42", "= 43")

    def exercise_live_scenarios() -> None:
        smoke_project = project
        smoke_root = project.parent
        original_smoke_source = smoke_file.read_text(encoding="utf-8")
        smoke_source_is_modified = False
        format_file = smoke_root / f".fslangmcp-live-format-{os.getpid()}.fs"
        format_file.write_text(
            "module FormatSmoke\n\nlet addOne value= value+1\n",
            encoding="utf-8",
        )

        command = (
            [dotnet, str(server), "--project", str(smoke_project)]
            if server.suffix.lower() == ".dll"
            else [str(server), "--project", str(smoke_project)]
        )
        mcp = JsonLineProcess(command, cwd=smoke_root, env=child_env)
        try:
            mcp.send(
                {
                    "jsonrpc": "2.0",
                    "id": 1,
                    "method": "initialize",
                    "params": {
                        "protocolVersion": "2024-11-05",
                        "capabilities": {},
                        "clientInfo": {"name": "live-fsac-smoke", "version": "1"},
                    },
                }
            )
            # `--project` performs its readiness preload before the MCP host reads
            # initialize. Evaluated-source loading, FSAC initialize,
            # workspaceLoad, and the final ProjInfo readiness probe are
            # sequential bounded phases. Each may consume the configured timeout,
            # so the smoke must allow their combined supported budget.
            initialize = mcp.receive(1, args.timeout_seconds * 4.0)
            if "error" in initialize:
                fail(f"MCP initialize failed: {initialize['error']}")
            mcp.send(
                {
                    "jsonrpc": "2.0",
                    "method": "notifications/initialized",
                    "params": {},
                }
            )

            request_id = 2

            def invoke(tool_name: str, arguments: dict, timeout: float | None = None) -> object:
                nonlocal request_id
                response = call_tool(
                    mcp,
                    request_id,
                    tool_name,
                    arguments,
                    timeout if timeout is not None else args.timeout_seconds,
                )
                request_id += 1
                return response

            preload_rename = invoke(
                "textDocument_rename",
                {
                    "path": str(smoke_file),
                    "line": 14,
                    "character": 5,
                    "newName": "currentPreloadSmoke",
                },
            )
            preload_generation = require_contextual_response(
                preload_rename, smoke_project, "CLI --project preload rename"
            )
            if not contains_new_text(preload_rename.get("result"), "currentPreloadSmoke"):
                fail(
                    "CLI --project preload did not make the project usable before "
                    f"set_project: {preload_rename!r}"
                )

            set_project = invoke(
                "set_project",
                {"projectPath": str(smoke_project), "restartLsp": True},
            )
            if not isinstance(set_project, dict) or set_project.get("status") != "ok":
                fail(f"set_project did not succeed: {set_project!r}")
            details = set_project.get("result")
            if not isinstance(details, dict):
                fail(f"set_project has no result object: {set_project!r}")
            readiness = details.get("readiness")
            if (
                details.get("workspaceLoadStatus") != "ready"
                or not isinstance(readiness, dict)
                or readiness.get("lsp") is not True
                or readiness.get("projectOptions") is not True
            ):
                fail(f"live FSAC/project-options readiness failed: {details!r}")
            loaded = details.get("loadedProjects")
            if not isinstance(loaded, list) or not any(
                isinstance(path, str) and paths_equal(path, smoke_project)
                for path in loaded
            ):
                fail(f"FSAC did not load {smoke_project}: {loaded!r}")
            initial_generation = details.get("sessionGeneration")
            if not isinstance(initial_generation, int) or initial_generation <= 0:
                fail(f"set_project omitted sessionGeneration: {details!r}")
            if initial_generation <= preload_generation:
                fail(
                    "set_project(restartLsp=true) did not replace the CLI-preloaded "
                    f"FSAC generation: before={preload_generation}, after={initial_generation}"
                )

            integrated_proj_info = invoke(
                "fcs_get_project_options", {"projectPath": str(smoke_project)}
            )
            if (
                not isinstance(integrated_proj_info, dict)
                or not paths_equal(
                    str(integrated_proj_info.get("projectPath", "")), smoke_project
                )
                or not isinstance(integrated_proj_info.get("otherOptions"), list)
                or not integrated_proj_info["otherOptions"]
                or not isinstance(integrated_proj_info.get("optionsCount"), int)
                or integrated_proj_info["optionsCount"] <= 0
            ):
                fail(
                    "FsLangMCP returned invalid evaluated project options: "
                    f"{integrated_proj_info!r}"
                )

            # FSAC 0.83 publishes versionless diagnostics. Make the deliberate
            # error a real disk input before the diagnostics generation is
            # established so the bridge can prove freshness against its strong
            # generation-start hash; an unsaved versionless publication must stay
            # untrusted by design.
            smoke_file.write_text(broken_source, encoding="utf-8")
            smoke_source_is_modified = True
            diagnostics_glob = smoke_file.name

            diagnostics_rebase = invoke(
                "check",
                {
                    "scope": "workspace",
                    "projectPath": str(smoke_project),
                    "fileGlob": diagnostics_glob,
                    "speed": "fast",
                    "severity": "all",
                },
            )
            diagnostics_generation = require_fsac_rebase(
                diagnostics_rebase,
                expected_project=smoke_project,
                expected_glob=diagnostics_glob,
                previous_generation=initial_generation,
            )

            diagnostics_sync = invoke(
                "textDocument_rename",
                {
                    "path": str(smoke_file),
                    "line": 2,
                    "character": 5,
                    "newName": "currentDiagnosticsSmoke",
                    "text": tainted_source,
                },
            )
            sync_generation = require_contextual_response(
                diagnostics_sync, smoke_project, "diagnostics didOpen/didChange"
            )
            if sync_generation != diagnostics_generation:
                fail(
                    "diagnostics document sync changed FSAC generation unexpectedly: "
                    f"expected {diagnostics_generation}, got {sync_generation}"
                )
            if not contains_new_text(
                diagnostics_sync.get("result"), "currentDiagnosticsSmoke"
            ):
                fail(
                    "the deliberately different unsaved document was not synchronized "
                    "through the live "
                    f"FSAC rename path: {diagnostics_sync!r}"
                )

            # didOpen/didChange deliberately taints a versionless FSAC
            # generation: a delayed publication cannot prove which document
            # content it analyzed. Exercise the recovery contract and bind all
            # subsequent diagnostics to the replacement generation.
            diagnostics_after_sync_rebase = invoke(
                "check",
                {
                    "scope": "workspace",
                    "projectPath": str(smoke_project),
                    "fileGlob": diagnostics_glob,
                    "speed": "fast",
                    "severity": "all",
                },
            )
            diagnostics_generation = require_fsac_rebase(
                diagnostics_after_sync_rebase,
                expected_project=smoke_project,
                expected_glob=diagnostics_glob,
                previous_generation=diagnostics_generation,
            )

            # The rebase clears the tainted document. Reopen from disk without a
            # text override: the first didOpen now exactly matches both the new
            # generation baseline and the stable disk hash, so versionless FSAC
            # diagnostics are provably content-equivalent and need no third
            # restart.
            diagnostics_baseline_reopen = invoke(
                "textDocument_rename",
                {
                    "path": str(smoke_file),
                    "line": 2,
                    "character": 5,
                    "newName": "currentBaselineSmoke",
                },
            )
            reopened_generation = require_contextual_response(
                diagnostics_baseline_reopen,
                smoke_project,
                "diagnostics baseline-equivalent didOpen",
            )
            if reopened_generation != diagnostics_generation:
                fail(
                    "baseline-equivalent didOpen changed FSAC generation unexpectedly: "
                    f"expected {diagnostics_generation}, got {reopened_generation}"
                )
            if not contains_new_text(
                diagnostics_baseline_reopen.get("result"), "currentBaselineSmoke"
            ):
                fail(
                    "the baseline-equivalent disk document was not reopened through "
                    f"the live FSAC rename path: {diagnostics_baseline_reopen!r}"
                )

            diagnostics_deadline = time.monotonic() + min(args.timeout_seconds, 60.0)
            diagnostics = None
            diagnostics_error = "FSAC did not publish diagnostics"

            while True:
                diagnostics = invoke(
                    "check",
                    {
                        "scope": "workspace",
                        "projectPath": str(smoke_project),
                        "fileGlob": diagnostics_glob,
                        "speed": "fast",
                        "severity": "all",
                    },
                )

                try:
                    require_fsac_diagnostics(
                        diagnostics,
                        expected_project=smoke_project,
                        expected_file=smoke_file,
                        expected_glob=diagnostics_glob,
                        expected_generation=diagnostics_generation,
                    )
                    break
                except RuntimeError as error:
                    diagnostics_error = str(error)

                if time.monotonic() >= diagnostics_deadline:
                    fail(
                        "timed out waiting for context-bound FSAC publishDiagnostics; "
                        f"last validation error: {diagnostics_error}; "
                        f"payload={diagnostics!r}; server_stderr={mcp.stderr_tail()!r}"
                    )
                time.sleep(0.25)

            smoke_file.write_text(original_smoke_source, encoding="utf-8")
            smoke_source_is_modified = False

            before_crash = invoke("fsharp_runtime_status", {})
            original_fsac_pid = child_pid(before_crash)
            if original_fsac_pid is None or original_fsac_pid == mcp.process.pid:
                fail(f"could not identify a distinct FSAC child: {before_crash!r}")

            crash_signal = getattr(signal, "SIGKILL", signal.SIGTERM)
            os.kill(original_fsac_pid, crash_signal)

            crash_deadline = time.monotonic() + 15.0
            while True:
                after_crash = invoke("fsharp_runtime_status", {}, timeout=10)
                if child_pid(after_crash) is None:
                    break
                if time.monotonic() >= crash_deadline:
                    fail(f"FSAC pid {original_fsac_pid} did not exit after forced crash")
                time.sleep(0.2)

            recovered = invoke(
                "textDocument_rename",
                {
                    "path": str(smoke_file),
                    "line": 14,
                    "character": 5,
                    "newName": "currentSmoke",
                },
            )
            recovered_generation = (
                recovered.get("sessionGeneration") if isinstance(recovered, dict) else None
            )
            if (
                not isinstance(recovered, dict)
                or recovered.get("status") != "ok"
                or not contains_new_text(recovered.get("result"), "currentSmoke")
                or not isinstance(recovered_generation, int)
                or recovered_generation <= diagnostics_generation
            ):
                fail(f"FSAC did not recover transparently after the crash: {recovered!r}")

            after_recovery = invoke("fsharp_runtime_status", {})
            replacement_fsac_pid = child_pid(after_recovery)
            if replacement_fsac_pid is None or replacement_fsac_pid == original_fsac_pid:
                fail(
                    "FSAC recovery did not produce a replacement child process: "
                    f"before={original_fsac_pid}, after={replacement_fsac_pid}"
                )

            formatting = invoke(
                "textDocument_formatting",
                {"path": str(format_file)},
            )
            formatted = formatting.get("result") if isinstance(formatting, dict) else None
            if (
                not isinstance(formatting, dict)
                or formatting.get("status") != "ok"
                or not isinstance(formatted, dict)
                or not isinstance(formatted.get("formatted"), str)
                or "let addOne value = value + 1" not in formatted["formatted"]
                or not isinstance(formatted.get("edits"), list)
                or not formatted["edits"]
            ):
                fail(f"live formatting did not return the expected edits: {formatting!r}")
        finally:
            mcp.close()
            if smoke_source_is_modified:
                smoke_file.write_text(original_smoke_source, encoding="utf-8")
            format_file.unlink(missing_ok=True)

        if mcp.process.returncode != 0:
            fail(
                f"FsLangMCP exited with {mcp.process.returncode}; "
                f"stderr={mcp.stderr_tail()!r}"
            )

    exercise_live_scenarios()

    print(f"sdk={run_checked([dotnet, '--version'], cwd=project.parent).stdout.strip()}")
    print(f"fantomas={versions['fantomas'][0]}")
    print(f"fsautocomplete={versions['fsautocomplete'][0]}")
    print(f"ionide.projinfo.tool={versions['ionide.projinfo.tool'][0]}")
    print(
        "live_fsac=set_project ready; diagnostics=ready; rename=ready; "
        "format=ready; crash_restart=ready; proj_info=ready"
    )


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser()
    parser.add_argument("--manifest", default=str(DEFAULT_MANIFEST))
    parser.add_argument("--dotnet")
    subparsers = parser.add_subparsers(dest="command", required=True)

    install = subparsers.add_parser("install")
    install.add_argument("--tool-path", required=True)
    install.add_argument("--timeout-seconds", type=float, default=300)
    install.set_defaults(run=install_tools)

    smoke = subparsers.add_parser("smoke")
    smoke.add_argument("--tool-path", required=True)
    smoke.add_argument("--server", required=True)
    smoke.add_argument("--project", default=str(REPOSITORY_ROOT / "FsLangMcp.fsproj"))
    smoke.add_argument("--timeout-seconds", type=float, default=150)
    smoke.set_defaults(run=smoke_tools)
    return parser


def main() -> int:
    args = build_parser().parse_args()
    args.run(args)
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (RuntimeError, OSError, ValueError, subprocess.SubprocessError) as error:
        print(f"error: {error}", file=sys.stderr)
        raise SystemExit(1)
