#!/usr/bin/env python3
"""Verify the version baked into an FsLangMcp dotnet-tool package.

The script uses only the Python standard library. It checks the NuGet metadata,
installs the exact local package into an isolated tool directory when needed,
checks `fslangmcp --version`, `initialize.serverInfo.version`, and the packaged
`fslangmcp_version` MCP tool. This catches the release failure where
PackageVersion is changed at pack time while an older assembly is reused.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import queue
import re
import shutil
import subprocess
import sys
import tempfile
import threading
import time
from typing import NoReturn
import xml.etree.ElementTree as ET
from xml.sax.saxutils import quoteattr
import zipfile


SEMVER = re.compile(
    r"^(0|[1-9][0-9]*)\."
    r"(0|[1-9][0-9]*)\."
    r"(0|[1-9][0-9]*)"
    r"(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$"
)


def fail(message: str) -> NoReturn:
    raise RuntimeError(message)


def nuspec_version(package: Path) -> str:
    with zipfile.ZipFile(package) as archive:
        nuspecs = [name for name in archive.namelist() if name.endswith(".nuspec")]
        if len(nuspecs) != 1:
            fail(f"expected one .nuspec in {package}, found {len(nuspecs)}")

        root = ET.fromstring(archive.read(nuspecs[0]))
        version = root.findtext(".//{*}metadata/{*}version")
        if not version:
            fail(f"package metadata in {package} has no version")
        return version


def packaged_runtime_size(package: Path) -> int:
    with zipfile.ZipFile(package) as archive:
        runtime_entries = [
            entry
            for entry in archive.infolist()
            if entry.filename.endswith("/FsLangMcp.dll")
        ]
        if len(runtime_entries) != 1:
            fail(
                f"expected one packaged FsLangMcp.dll in {package}, "
                f"found {len(runtime_entries)}"
            )

        size = runtime_entries[0].file_size
        if size <= 0:
            fail(f"packaged FsLangMcp.dll is empty in {package}")
        return size


def read_json_response(
    responses: "queue.Queue[str | None]", expected_id: int, timeout_seconds: float
) -> dict:
    deadline = time.monotonic() + timeout_seconds
    seen: list[str] = []

    while True:
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            fail(
                f"timed out waiting for MCP response id={expected_id}; "
                f"stdout seen: {seen!r}"
            )

        try:
            line = responses.get(timeout=remaining)
        except queue.Empty:
            continue

        if line is None:
            fail(
                f"packaged tool closed stdout before MCP response id={expected_id}; "
                f"stdout seen: {seen!r}"
            )

        stripped = line.strip()
        if not stripped:
            continue

        seen.append(stripped)
        try:
            message = json.loads(stripped)
        except json.JSONDecodeError:
            continue

        if message.get("id") == expected_id:
            return message


def verify_initialize_server_version(initialize: dict, expected_version: str) -> str:
    result = initialize.get("result")
    server_info = result.get("serverInfo") if isinstance(result, dict) else None
    server_version = server_info.get("version") if isinstance(server_info, dict) else None

    if server_version != expected_version:
        fail(
            "packaged initialize.serverInfo.version mismatch: "
            f"expected {expected_version!r}, got {server_version!r}"
        )

    return server_version


def verify_installed_tool(tool: Path, expected_version: str) -> tuple[str, str]:
    process = subprocess.Popen(
        [str(tool)],
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        encoding="utf-8",
        bufsize=1,
    )

    if process.stdin is None or process.stdout is None or process.stderr is None:
        fail("failed to open packaged tool stdio")

    responses: "queue.Queue[str | None]" = queue.Queue()
    stderr_lines: list[str] = []

    def read_stdout() -> None:
        try:
            for line in process.stdout:
                responses.put(line)
        finally:
            responses.put(None)

    def read_stderr() -> None:
        stderr_lines.extend(process.stderr)

    stdout_thread = threading.Thread(target=read_stdout, daemon=True)
    stderr_thread = threading.Thread(target=read_stderr, daemon=True)
    stdout_thread.start()
    stderr_thread.start()

    def send(message: dict) -> None:
        process.stdin.write(json.dumps(message, separators=(",", ":")) + "\n")
        process.stdin.flush()

    try:
        send(
            {
                "jsonrpc": "2.0",
                "id": 1,
                "method": "initialize",
                "params": {
                    "protocolVersion": "2024-11-05",
                    "capabilities": {},
                    "clientInfo": {"name": "release-version-smoke", "version": "1"},
                },
            }
        )
        initialize = read_json_response(responses, 1, 20.0)
        if "error" in initialize:
            fail(f"MCP initialize failed: {initialize['error']}")
        server_info_version = verify_initialize_server_version(
            initialize, expected_version
        )

        send({"jsonrpc": "2.0", "method": "notifications/initialized", "params": {}})
        send(
            {
                "jsonrpc": "2.0",
                "id": 2,
                "method": "tools/call",
                "params": {"name": "fslangmcp_version", "arguments": {}},
            }
        )
        version_response = read_json_response(responses, 2, 20.0)
        if "error" in version_response:
            fail(f"fslangmcp_version failed: {version_response['error']}")

        content = version_response.get("result", {}).get("content", [])
        text_blocks = [block.get("text") for block in content if block.get("type") == "text"]
        if len(text_blocks) != 1 or not text_blocks[0]:
            fail(f"unexpected fslangmcp_version response: {version_response}")

        version_payload = json.loads(text_blocks[0])
        if not isinstance(version_payload, dict):
            fail(f"unexpected fslangmcp_version payload: {version_payload!r}")

        product_version = version_payload.get("fslangmcpVersion")
        if product_version != expected_version:
            fail(
                "packaged runtime version mismatch: "
                f"expected {expected_version!r}, got {product_version!r}"
            )

        return server_info_version, product_version
    finally:
        try:
            process.stdin.close()
        except OSError:
            pass

        try:
            process.wait(timeout=10)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait(timeout=5)

        stdout_thread.join(timeout=1)
        stderr_thread.join(timeout=1)

        if process.returncode not in (0, None):
            fail(
                f"packaged tool exited with {process.returncode}; "
                f"stderr: {''.join(stderr_lines).strip()}"
            )


def verify_cli_version(tool: Path, expected_version: str) -> str:
    completed = subprocess.run(
        [str(tool), "--version"],
        text=True,
        encoding="utf-8",
        capture_output=True,
        timeout=30,
    )
    if completed.returncode != 0:
        fail(
            f"packaged tool --version exited with {completed.returncode}:\n"
            f"{completed.stdout}\n{completed.stderr}"
        )

    actual_version = completed.stdout.strip()
    if actual_version != expected_version:
        fail(
            "packaged CLI version mismatch: "
            f"expected {expected_version!r}, got {actual_version!r}"
        )

    return actual_version


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--package", required=True, type=Path)
    parser.add_argument("--expected-version", required=True)
    parser.add_argument("--installed-tool", type=Path)
    parser.add_argument("--write-sha256", type=Path)
    args = parser.parse_args()

    package = args.package.resolve()
    expected_version = args.expected_version

    if not SEMVER.fullmatch(expected_version):
        fail(f"expected version is not a supported SemVer release: {expected_version!r}")
    if not package.is_file():
        fail(f"package does not exist: {package}")

    metadata_version = nuspec_version(package)
    if metadata_version != expected_version:
        fail(
            f"NuGet metadata version mismatch: expected {expected_version!r}, "
            f"got {metadata_version!r}"
        )
    runtime_size = packaged_runtime_size(package)

    dotnet = os.environ.get("DOTNET_HOST_PATH") or shutil.which("dotnet")
    if not dotnet:
        fail("dotnet was not found on PATH")

    if args.installed_tool:
        executable = args.installed_tool.resolve()
        if not executable.is_file():
            fail(f"installed tool does not exist: {executable}")
        cli_version = verify_cli_version(executable, expected_version)
        server_info_version, runtime_version = verify_installed_tool(
            executable, expected_version
        )
    else:
        with tempfile.TemporaryDirectory(prefix="fslangmcp-package-smoke-") as temp_name:
            temp = Path(temp_name)
            tool_dir = temp / "tool"
            config = temp / "NuGet.config"
            config.write_text(
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n"
                "<configuration><packageSources><clear />"
                f"<add key=\"local\" value={quoteattr(str(package.parent))} />"
                "</packageSources></configuration>\n",
                encoding="utf-8",
            )

            install = subprocess.run(
                [
                    dotnet,
                    "tool",
                    "install",
                    "--tool-path",
                    str(tool_dir),
                    "--configfile",
                    str(config),
                    "--no-http-cache",
                    "FsLangMcp",
                    "--version",
                    expected_version,
                ],
                text=True,
                encoding="utf-8",
                capture_output=True,
                timeout=120,
                env=os.environ
                | {
                    "DOTNET_CLI_HOME": str(temp / "dotnet-home"),
                    "NUGET_PACKAGES": str(temp / "packages"),
                },
            )
            if install.returncode != 0:
                fail(
                    f"failed to install local package ({install.returncode}):\n"
                    f"{install.stdout}\n{install.stderr}"
                )

            executable = tool_dir / (
                "fslangmcp.exe" if os.name == "nt" else "fslangmcp"
            )
            if not executable.is_file():
                fail(f"tool install did not create {executable}")

            cli_version = verify_cli_version(executable, expected_version)
            server_info_version, runtime_version = verify_installed_tool(
                executable, expected_version
            )

    digest = hashlib.sha256(package.read_bytes()).hexdigest()
    if args.write_sha256:
        checksum_path = args.write_sha256.resolve()
        checksum_path.parent.mkdir(parents=True, exist_ok=True)
        checksum_path.write_text(f"{digest}  {package.name}\n", encoding="utf-8")

    print(f"package={package.name}")
    print(f"nuspec_version={metadata_version}")
    print(f"runtime_size={runtime_size}")
    print(f"cli_version={cli_version}")
    print(f"server_info_version={server_info_version}")
    print(f"runtime_version={runtime_version}")
    print(f"sha256={digest}")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (
        RuntimeError,
        ET.ParseError,
        OSError,
        ValueError,
        subprocess.SubprocessError,
        zipfile.BadZipFile,
    ) as error:
        print(f"error: {error}", file=sys.stderr)
        raise SystemExit(1)
