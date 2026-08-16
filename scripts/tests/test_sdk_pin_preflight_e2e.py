"""End-to-end guard for the #192 acceptance criterion.

Drives the built server over raw stdio JSON-RPC (the MCP newline framing) against a
throwaway project whose `global.json` pins an SDK version no machine can have, and
asserts the whole acceptance sentence: the poisoned `set_project` returns the typed
`sdk_not_found` envelope, the server stays alive, and it still answers on the same
connection afterwards.

This is the only check that exercises the real transport. The in-process xUnit tests
(`tests/FsLangMcp.Tests/SdkPreflightTests.fs`) cover the verdict logic and the
pre-flight call sites; they cannot observe what an MCP client actually receives.

Needs a built server assembly. With `FSLANGMCP_SERVER_DLL` unset it looks for
`bin/{Release,Debug}/net10.0/FsLangMcp.dll` and *skips* when neither exists, so
`python -m unittest discover -s scripts/tests` stays runnable before a build. Setting
`FSLANGMCP_SERVER_DLL` is an explicit "run this": a missing file then raises rather
than skipping, so path drift cannot turn the dedicated CI step green while the
acceptance guard silently evaporates.
"""

from __future__ import annotations

import json
import os
import queue
import shutil
import subprocess
import tempfile
import threading
import time
import unittest
from pathlib import Path

REPOSITORY_ROOT = Path(__file__).resolve().parents[2]


def canonical_path(value: str | Path) -> str:
    r"""Compare paths, not spellings — apply to BOTH sides of any path equality.

    A Windows CI runner sets TEMP to the 8.3 short form, so `tempfile.mkdtemp()`
    handed this test `C:\Users\RUNNER~1\AppData\Local\Temp\...\global.json` while
    the server echoed the same file back expanded, as
    `C:\Users\runneradmin\...\global.json` — one file, two spellings, red job
    (PR #200). Which side does the expanding is not worth encoding here: run both
    through the same funnel and the question stops mattering.

    `realpath` resolves 8.3 short names on Windows (it goes through
    `nt._getfinalpathname`, i.e. `GetFinalPathNameByHandle`, which returns the
    canonical long form) and follows the macOS `/var` -> `/private/var` symlink;
    `normcase` absorbs Windows case and separator differences. Canonicalising only
    one side would just move the bug.
    """
    return os.path.normcase(os.path.realpath(str(value)))


# No machine can have this installed, so the poisoned side of the assertion holds
# regardless of which SDKs the host actually has.
ABSENT_SDK_VERSION = "999.999.999"

REQUEST_TIMEOUT_SECONDS = 120.0

GLOBAL_JSON = json.dumps(
    {"sdk": {"version": ABSENT_SDK_VERSION, "rollForward": "disable"}}, indent=2
)

FSPROJ = """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="Library.fs" />
  </ItemGroup>
</Project>
"""

LIBRARY_FS = "module Poison.Library\n\nlet answer = 42\n"


def locate_server_dll() -> Path | None:
    override = os.environ.get("FSLANGMCP_SERVER_DLL")

    if override:
        # Resolve against the caller's cwd: the server is started inside a temp
        # directory, so a relative path would not survive the spawn.
        candidate = Path(override).resolve()

        if not candidate.is_file():
            # Setting the variable is an explicit "run this". Skipping here would let
            # path drift (a TFM bump the workflow env missed) silently turn the
            # dedicated CI step green while the acceptance guard evaporates. The skip
            # below exists only for the pre-build discovery run, which never sets it.
            raise FileNotFoundError(
                f"FSLANGMCP_SERVER_DLL is set to {override!r} (resolved to {candidate}) "
                "but no file is there — build the server or correct the path."
            )

        return candidate

    for configuration in ("Release", "Debug"):
        candidate = REPOSITORY_ROOT / "bin" / configuration / "net10.0" / "FsLangMcp.dll"

        if candidate.is_file():
            return candidate

    return None


class StdioClient:
    """Minimal MCP stdio client: newline-delimited JSON-RPC over the child's pipes."""

    def __init__(self, server_dll: Path, working_directory: Path) -> None:
        self._process = subprocess.Popen(
            ["dotnet", str(server_dll)],
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.DEVNULL,
            cwd=str(working_directory),
            text=True,
            bufsize=1,
        )
        self._responses: "queue.Queue[str | None]" = queue.Queue()
        self._next_id = 0
        threading.Thread(target=self._pump_stdout, daemon=True).start()

    def _pump_stdout(self) -> None:
        assert self._process.stdout is not None

        for line in self._process.stdout:
            self._responses.put(line.rstrip("\n"))

        self._responses.put(None)

    def _write(self, payload: dict) -> None:
        assert self._process.stdin is not None
        self._process.stdin.write(json.dumps(payload) + "\n")
        self._process.stdin.flush()

    def notify(self, method: str, params: dict) -> None:
        self._write({"jsonrpc": "2.0", "method": method, "params": params})

    def request(self, method: str, params: dict) -> dict:
        self._next_id += 1
        request_id = self._next_id
        self._write({"jsonrpc": "2.0", "id": request_id, "method": method, "params": params})
        deadline = time.monotonic() + REQUEST_TIMEOUT_SECONDS

        while True:
            remaining = deadline - time.monotonic()

            if remaining <= 0:
                raise AssertionError(f"timed out waiting for a response to {method}")

            line = self._responses.get(timeout=remaining)

            if line is None:
                raise AssertionError(
                    f"the server closed stdout before answering {method} "
                    f"(exit code {self._process.poll()})"
                )

            try:
                message = json.loads(line)
            except json.JSONDecodeError as error:
                # Anything non-JSON on stdout is transport corruption — the exact
                # failure mode #192 was first suspected of. Surface it, never skip it.
                raise AssertionError(
                    f"non-JSON line on the server's stdout while awaiting {method}: {line!r}"
                ) from error

            if message.get("id") == request_id:
                return message

    def call_tool(self, name: str, arguments: dict) -> tuple[dict, bool]:
        """Returns (parsed tool payload, isError)."""
        message = self.request("tools/call", {"name": name, "arguments": arguments})
        result = message.get("result")

        if result is None:
            raise AssertionError(f"{name} returned no result: {message}")

        text = result["content"][0]["text"]

        try:
            payload = json.loads(text)
        except json.JSONDecodeError:
            # An `isError` result carries a non-JSON prefix; keep it visible.
            payload = {"raw": text}

        return payload, bool(result.get("isError", False))

    @property
    def alive(self) -> bool:
        return self._process.poll() is None

    def close(self) -> int | None:
        if self._process.stdin is not None:
            try:
                self._process.stdin.close()
            except OSError:
                pass

        try:
            exit_code = self._process.wait(timeout=30)
        except subprocess.TimeoutExpired:
            self._process.kill()
            exit_code = None

        if self._process.stdout is not None:
            try:
                self._process.stdout.close()
            except OSError:
                pass

        return exit_code


@unittest.skipUnless(shutil.which("dotnet"), "dotnet is not on PATH")
@unittest.skipUnless(
    locate_server_dll() is not None,
    "no built FsLangMcp.dll found (build first, or set FSLANGMCP_SERVER_DLL)",
)
class SdkPinPreflightEndToEnd(unittest.TestCase):
    def setUp(self) -> None:
        self._root = Path(tempfile.mkdtemp(prefix="fslangmcp_sdk_pin_e2e_"))
        (self._root / "global.json").write_text(GLOBAL_JSON, encoding="utf-8")
        (self._root / "Poison.fsproj").write_text(FSPROJ, encoding="utf-8")
        (self._root / "Library.fs").write_text(LIBRARY_FS, encoding="utf-8")
        self._client = StdioClient(locate_server_dll(), self._root)
        self.addCleanup(shutil.rmtree, self._root, True)
        self.addCleanup(self._client.close)

        self._client.request(
            "initialize",
            {
                "protocolVersion": "2024-11-05",
                "capabilities": {},
                "clientInfo": {"name": "sdk-pin-e2e", "version": "0"},
            },
        )
        self._client.notify("notifications/initialized", {})

    def test_poisoned_set_project_is_typed_and_the_server_keeps_serving(self) -> None:
        project = str(self._root / "Poison.fsproj")

        # Twice, matching the field report in #100: the second call must behave like
        # the first rather than hitting a wedged gate or a poisoned cache.
        for attempt in (1, 2):
            with self.subTest(attempt=attempt):
                payload, is_error = self._client.call_tool("set_project", {"projectPath": project})

                self.assertFalse(is_error, f"expected a typed envelope, not a tool error: {payload}")
                self.assertEqual("infrastructure_error", payload.get("status"))
                self.assertEqual("sdk_not_found", payload.get("errorKind"))
                self.assertEqual(ABSENT_SDK_VERSION, payload.get("requestedSdkVersion"))
                reported_global_json = payload.get("globalJsonPath")
                self.assertIsNotNone(reported_global_json, f"no globalJsonPath in {payload}")
                self.assertEqual(
                    canonical_path(self._root / "global.json"),
                    canonical_path(reported_global_json),
                )
                self.assertIsInstance(payload.get("installedSdks"), list)
                self.assertEqual(2, len(payload.get("remedies", [])))
                self.assertIn(ABSENT_SDK_VERSION, payload.get("message", ""))
                self.assertTrue(self._client.alive, "the server must survive the poisoned call")

        # The acceptance criterion's second half: still serving on the same connection.
        version_payload, is_error = self._client.call_tool("fslangmcp_version", {})
        self.assertFalse(is_error)
        self.assertEqual("ok", version_payload.get("status"))
        self.assertTrue(self._client.alive)

    def test_project_health_reports_the_sdk_pin_as_the_blocking_reason(self) -> None:
        project = str(self._root / "Poison.fsproj")
        payload, is_error = self._client.call_tool("project_health", {"projectPath": project})

        self.assertFalse(is_error)
        reason = payload["toolingReadiness"]["fcs"]["reason"]
        # Substring checks on prose, deliberately NOT routed through canonical_path:
        # the haystack is a sentence containing a path, so canonicalising the needle
        # while the haystack keeps the server's spelling would reintroduce the same
        # mismatch in reverse. A version number and a bare filename are spelling-stable
        # on every host; the exact path is asserted in the test above.
        self.assertIn(ABSENT_SDK_VERSION, reason)
        self.assertIn("global.json", reason)
        self.assertTrue(self._client.alive)


if __name__ == "__main__":
    unittest.main()
