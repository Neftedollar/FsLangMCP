"""#247: test the registered description and lookup coordinates over real MCP stdio.

Pre-build discovery skips without a server; CI explicitly sets FSLANGMCP_SERVER_DLL
in its post-build step so a missing binary fails instead of silently skipping.
"""

import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path

from scripts.tests.test_sdk_pin_preflight_e2e import (
    FSPROJ,
    StdioClient,
    canonical_path,
    locate_server_dll,
)

SOURCE = "module Coordinates\nlet target = 10\nlet repeated = target + target\n"


@unittest.skipUnless(shutil.which("dotnet"), "dotnet is not on PATH")
@unittest.skipUnless(locate_server_dll() is not None, "build the MCP server first")
class SymbolCoordinatesEndToEnd(unittest.TestCase):
    def setUp(self):
        self.root = Path(tempfile.mkdtemp(prefix="fslangmcp_coordinates_"))
        self.addCleanup(shutil.rmtree, self.root, True)
        self.project = self.root / "Coordinates.fsproj"
        self.source = self.root / "Library.fs"
        self.project.write_text(FSPROJ, encoding="utf-8")
        self.source.write_text(SOURCE, encoding="utf-8")
        restored = subprocess.run(
            ["dotnet", "restore", str(self.project), "--ignore-failed-sources", "-p:NuGetAudit=false"],
            capture_output=True, text=True, timeout=120,
        )
        self.assertEqual(0, restored.returncode, restored.stdout + restored.stderr)
        self.client = StdioClient(locate_server_dll(), self.root)
        self.addCleanup(self.client.close)
        self.client.request("initialize", {
            "protocolVersion": "2024-11-05", "capabilities": {},
            "clientInfo": {"name": "symbol-coordinates-e2e", "version": "1"},
        })
        self.client.notify("notifications/initialized", {})

    def lookup(self, line, word, **extra):
        payload, is_error = self.client.call_tool("fcs_symbol_at_word", {
            "path": str(self.source), "projectPath": str(self.project),
            "line": line, "word": word, **extra,
        })
        self.assertFalse(is_error, payload)
        return payload

    def test_registered_description_states_each_coordinate_system(self):
        tools = self.client.request("tools/list", {})["result"]["tools"]
        tool = next(tool for tool in tools if tool["name"] == "fcs_symbol_at_word")
        description = tool["description"]
        for statement in (
            "line and occurrence are 0-based",
            "candidate line/columns are 0-based",
            "definitionRange lines are 1-based, columns 0-based",
            "omitted/negative occurrence requires a unique candidate",
        ):
            self.assertIn(statement, description)
        self.assertEqual("integer", tool["inputSchema"]["properties"]["line"]["type"])
        self.assertIn("line", tool["inputSchema"]["required"])

    def test_wire_lookup_preserves_zero_based_selection_and_fcs_definition_ranges(self):
        # Input zero really selects the first physical source line, not its neighbour.
        first = self.lookup(0, "Coordinates")
        self.assertEqual("ok", first["status"])
        self.assertEqual(0, first["candidate"]["line"])
        self.assertEqual(1, first["definitionRange"]["startLine"])

        line = SOURCE.splitlines()[2]
        starts = [line.index("target"), line.rindex("target")]
        for occurrence, start in enumerate(starts):
            with self.subTest(occurrence=occurrence):
                result = self.lookup(2, "target", occurrence=occurrence)
                self.assertEqual("ok", result["status"])
                self.assertEqual("Coordinates.target", result["fullName"])
                self.assertEqual(line, result["lineText"])
                self.assertEqual(2, result["line"])
                self.assertEqual({"occurrence": occurrence, "line": 2,
                                  "startColumn": start, "endColumn": start + len("target"),
                                  "text": "target"}, result["candidate"])
                definition = result["definitionRange"]
                self.assertEqual(canonical_path(self.source), canonical_path(definition.pop("file")))
                self.assertEqual({"startLine": 2, "startColumn": 4,
                                  "endLine": 2, "endColumn": 10}, definition)

        for options in ({}, {"occurrence": -1}, {"occurrence": -7}):
            with self.subTest(options=options):
                ambiguous = self.lookup(2, "target", **options)
                self.assertEqual("ambiguous_word", ambiguous["status"])
                self.assertEqual([0, 1], [c["occurrence"] for c in ambiguous["candidates"]])
                self.assertEqual(starts, [c["startColumn"] for c in ambiguous["candidates"]])
                # The same default/negative value is valid for one candidate.
                unique = self.lookup(1, "target", **options)
                self.assertEqual("ok", unique["status"])
                self.assertEqual(0, unique["candidate"]["occurrence"])

        unique_invalid = self.lookup(1, "target", occurrence=1)
        self.assertEqual("invalid_occurrence", unique_invalid["status"])
        self.assertEqual(1, unique_invalid["candidateCount"])

        invalid = self.lookup(2, "target", occurrence=2)
        self.assertEqual("invalid_occurrence", invalid["status"])
        self.assertEqual(2, invalid["candidateCount"])
        self.assertEqual("invalid_line", self.lookup(-1, "target")["status"])
        self.assertEqual("no_candidate", self.lookup(0, "target")["status"])


if __name__ == "__main__":
    unittest.main()
