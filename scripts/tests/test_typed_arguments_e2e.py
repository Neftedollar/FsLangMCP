"""#268: typed-tool binding diagnostics over real MCP stdio."""

import json
import shutil
import tempfile
import unittest
from pathlib import Path

from scripts.tests.test_sdk_pin_preflight_e2e import StdioClient, locate_server_dll


@unittest.skipUnless(shutil.which("dotnet"), "dotnet is not on PATH")
@unittest.skipUnless(locate_server_dll() is not None, "build the MCP server first")
class TypedArgumentsEndToEnd(unittest.TestCase):
    def setUp(self):
        directory = tempfile.TemporaryDirectory(prefix="fslangmcp_arguments_")
        self.addCleanup(directory.cleanup)
        self.root = Path(directory.name)
        self.source = self.root / "Input.fsx"
        self.source.write_text("module Input\nlet target = 10\n", encoding="utf-8")
        self.client = StdioClient(locate_server_dll(), self.root)
        self.addCleanup(self.client.close)
        self.client.request("initialize", {
            "protocolVersion": "2024-11-05", "capabilities": {},
            "clientInfo": {"name": "typed-arguments-e2e", "version": "1"},
        })
        self.client.notify("notifications/initialized", {})

    def call(self, name, arguments):
        return self.client.request("tools/call", {"name": name, "arguments": arguments})

    def binding_message(self, name, arguments):
        response = self.call(name, arguments)
        self.assertEqual("2.0", response["jsonrpc"])
        self.assertNotIn("error", response)
        result = response["result"]
        self.assertIs(result["isError"], True)
        kind, = result["content"]
        self.assertEqual("text", kind["type"])
        self.assertLess(len(json.dumps(response)), 1024)
        return kind["text"]

    def test_file_path_typo_names_unknown_and_required_argument(self):
        message = self.binding_message("fcs_file_outline", {"filePath": "must-not-echo-value" * 1000})
        self.assertIn("Unknown fields: filePath.", message)
        self.assertIn("Missing required fields: path.", message)
        self.assertNotIn("must-not-echo-value", message)

    def test_second_registration_names_its_schema_fields_and_wrong_type(self):
        name = "fcs_symbol_at_word"
        message = self.binding_message(name, {"path": str(self.source), "lineNumber": 0})
        self.assertIn("Unknown fields: lineNumber.", message)
        self.assertIn("Missing required fields: line.", message)
        typed = self.binding_message(name, {"path": str(self.source), "line": "not-a-number"})
        self.assertIn("Fields with incompatible JSON types: line.", typed)
        self.assertNotIn("not-a-number", typed)

    def test_null_required_and_oversized_or_unsafe_unknown_names(self):
        oversized = "oversized-key-marker" * 1000
        message = self.binding_message("fcs_file_outline", {
            "path": None, oversized: "arbitrary-value", "unsafe\nkey": True,
            **{f"extra{index:03}": index for index in range(50)},
        })
        self.assertIn("Missing required fields: path.", message)
        self.assertNotIn("oversized-key-marker", message)
        self.assertNotIn("unsafe", message)
        self.assertNotIn("arbitrary-value", message)

    def test_valid_calls_still_accept_extra_field(self):
        for args in ({"path": str(self.source)}, {"path": str(self.source), "filePath": "ignored-extra"}):
            with self.subTest(extra="filePath" in args):
                response = self.call("fcs_file_outline", args)
                self.assertNotIn("error", response)
                self.assertFalse(response["result"].get("isError", False), response)
                body = json.loads(response["result"]["content"][0]["text"])
                self.assertIn("Input", json.dumps(body))
                self.assertNotIn("ignored-extra", json.dumps(body))


if __name__ == "__main__":
    unittest.main()
