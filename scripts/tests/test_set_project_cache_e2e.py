"""Unchanged set_project selections may retain fresh FCS analysis, over real MCP stdio."""

import os
import shutil
import subprocess
import tempfile
import time
import unittest
from pathlib import Path
from unittest.mock import patch

from scripts.tests.test_sdk_pin_preflight_e2e import StdioClient, locate_server_dll


@unittest.skipUnless(shutil.which("dotnet"), "dotnet is not on PATH")
@unittest.skipUnless(locate_server_dll() is not None, "build the MCP server first")
class SetProjectCacheEndToEnd(unittest.TestCase):
    def setUp(self):
        temp = tempfile.TemporaryDirectory(prefix="fslangmcp_set_project_cache_")
        self.addCleanup(temp.cleanup)
        # Keep MSBuild's cwd and reference paths canonical on macOS (/var is an alias).
        self.root = Path(temp.name).resolve()

        def write(path, text):
            file = self.root / path
            file.parent.mkdir(parents=True, exist_ok=True)
            file.write_text(text, encoding="utf-8")
            return file

        def project(compile_file, references=""):
            return ('<Project Sdk="Microsoft.NET.Sdk">'
                    '<PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>'
                    f'<ItemGroup><Compile Include="{compile_file}" />{references}</ItemGroup></Project>')

        self.a_source = write("App/SourceA.fs", "module App\nlet Original = Dependency.OldName\n")
        write("App/SourceB.fs", "module App\nlet Alternate = Dependency.OldName\n")
        self.dep_source = write("Dep/Library.fs", "module Dependency\nlet OldName = 42\n")
        self.other_source = write("Other/Library.fs", "module Other\nlet Different = 7\n")
        self.app = write("App/App.fsproj", project(
            "SourceA.fs", '<ProjectReference Include="../Dep/Dep.fsproj" />'))
        self.dep = write("Dep/Dep.fsproj", project("Library.fs"))
        self.other = write("Other/Other.fsproj", project("Library.fs"))
        self.solution = write("Work.slnx", '<Solution><Project Path="App/App.fsproj" />'
                              '<Project Path="Dep/Dep.fsproj" /></Solution>')
        self.alt_root = self.root / "OtherRoot"
        self.alt_root.mkdir()
        built = subprocess.run(
            ["dotnet", "build", str(self.solution), "-m:1", "-nr:false",
             "-p:NuGetAudit=false", "-p:UseSharedCompilation=false"],
            capture_output=True, text=True, timeout=120,
        )
        self.assertEqual(0, built.returncode, built.stdout + built.stderr)
        restored = subprocess.run(
            ["dotnet", "restore", str(self.other), "-m:1", "-nr:false", "-p:NuGetAudit=false"],
            capture_output=True, text=True, timeout=120,
        )
        self.assertEqual(0, restored.returncode, restored.stdout + restored.stderr)
        self.start_client(warmup=False)

    def start_client(self, *, warmup):
        # These existing retention tests still assert cold misses. The dedicated
        # warm-up test below exercises the default independently.
        with patch.dict(os.environ):
            if warmup:
                os.environ.pop("FSLANGMCP_SEMANTIC_WARMUP", None)
            else:
                os.environ["FSLANGMCP_SEMANTIC_WARMUP"] = "0"
            self.client = StdioClient(locate_server_dll(), self.root)
        self.addCleanup(self.client.close)
        self.client.request("initialize", {
            "protocolVersion": "2024-11-05", "capabilities": {},
            "clientInfo": {"name": "set-project-cache-e2e", "version": "1"},
        })
        self.client.notify("notifications/initialized", {})

    def call(self, tool, args, expected="succeeded"):
        started = time.monotonic()
        payload, error = self.client.call_tool(tool, args)
        elapsed = round((time.monotonic() - started) * 1000)
        self.assertFalse(error, payload)
        self.assertEqual(expected, payload["status"], payload)
        return payload, elapsed

    def select(self, project, workspace=None, expected="ok"):
        args = {"projectPath": str(project), "restartLsp": False}
        if workspace is not None:
            args["workspacePath"] = str(workspace)
        return self.call("set_project", args, expected=expected)

    def find(self, query, project=None):
        args = {"query": query, "kind": "definition"}
        if project is not None:
            args.update({"projectPath": str(project), "scope": "project"})
        return self.call("find", args)

    def assert_uses(self, result, *, hits=0, misses=0):
        cache = result["cacheState"]["projectUses"]
        self.assertEqual((hits, misses, 0), (cache["hits"], cache["misses"], cache["incomplete"]))

    def replace_same_stat(self, path, old, new):
        initial = path.stat()
        text = path.read_text(encoding="utf-8")
        self.assertIn(old, text)
        path.write_text(text.replace(old, new), encoding="utf-8")
        os.utime(path, ns=(initial.st_atime_ns, initial.st_mtime_ns))
        after = path.stat()
        self.assertEqual((initial.st_size, initial.st_mtime_ns), (after.st_size, after.st_mtime_ns))

    def test_default_selection_warms_first_find_and_edits_invalidate_without_reselecting(self):
        self.client.close()
        self.start_client(warmup=True)
        selected, _ = self.select(self.solution, self.root)
        self.assertEqual({"selected": 2, "warmed": 2, "complete": True},
                         selected["result"].get("semanticWarmup"))
        first, _ = self.find("Original")
        self.assert_uses(first, hits=2)
        self.assertEqual(1, first["returnedSiteCount"])

        self.replace_same_stat(self.a_source, "Original", "Replaced")
        edited, _ = self.find("Replaced")
        self.assert_uses(edited, hits=1, misses=1)
        self.assertEqual(1, edited["returnedSiteCount"])

        self.replace_same_stat(self.dep_source, "OldName", "NewName")
        changed, _ = self.find("Replaced", self.app)
        self.assert_uses(changed, misses=1)
        self.assertEqual(1, changed["returnedSiteCount"])
        self.assertGreater(changed["projectDiagnosticsTotalCount"], 0)

    def test_same_effective_selection_keeps_hits_but_never_stale_source_or_project(self):
        _, setup_ms = self.select(self.app, self.root)
        cold, cold_ms = self.find("Original")
        self.assert_uses(cold, misses=1)
        self.assertEqual(1, cold["returnedSiteCount"])
        warm, warm_ms = self.find("Original")
        self.assert_uses(warm, hits=1)

        # Re-selected spellings normalize to the same project and workspace.
        _, same_ms = self.select("App/../App/App.fsproj", str(self.root) + "/./")
        same, same_find_ms = self.find("Original")
        actual = same["cacheState"]["projectUses"]
        print(f"set_project first/repeat={setup_ms}/{same_ms}ms; find cold/warm/after-repeat="
              f"{cold_ms}/{warm_ms}/{same_find_ms}ms; after-repeat={actual['hits']}hits/{actual['misses']}misses")
        self.assert_uses(same, hits=1)
        self.assertEqual(cold["sites"], same["sites"])

        self.select("Missing.fsproj", expected="invalid_args")
        after_rejection, _ = self.find("Original")
        self.assert_uses(after_rejection, hits=1)

        self.replace_same_stat(self.a_source, "Original", "Replaced")
        self.select(self.app, self.root)
        edited, _ = self.find("Replaced")
        self.assert_uses(edited, misses=1)
        self.assertEqual(1, edited["returnedSiteCount"])
        old, _ = self.find("Original")
        self.assertEqual(0, old["returnedSiteCount"])

        self.replace_same_stat(self.app, "SourceA.fs", "SourceB.fs")
        self.select(self.app, self.root)
        project_edit, _ = self.find("Alternate")
        self.assert_uses(project_edit, misses=1)
        self.assertEqual(1, project_edit["returnedSiteCount"])
        self.assertTrue(project_edit["sites"][0]["file"].endswith("SourceB.fs"))
        edited_missing, _ = self.find("Replaced")
        self.assertEqual(0, edited_missing["returnedSiteCount"])

    def test_changed_root_project_and_members_clear_and_dependency_content_is_fresh(self):
        self.select(self.solution, self.root)
        cold, _ = self.find("OldName")
        self.assert_uses(cold, misses=2)
        self.assertEqual(1, cold["returnedSiteCount"])
        self.select(self.solution, self.root)
        warm_consumer, _ = self.find("Original", self.app)
        self.assert_uses(warm_consumer, hits=1)

        self.replace_same_stat(self.dep_source, "OldName", "NewName")
        self.select(self.solution, self.root)
        changed_consumer, _ = self.find("Original", self.app)
        self.assert_uses(changed_consumer, misses=1)
        self.assertGreater(changed_consumer["projectDiagnosticsTotalCount"], 0)
        changed_definition, _ = self.find("NewName")
        self.assertEqual(1, changed_definition["returnedSiteCount"])
        old_definition, _ = self.find("OldName")
        self.assertEqual(0, old_definition["returnedSiteCount"])

        self.select(self.solution, self.alt_root)
        changed_root, _ = self.find("NewName")
        self.assert_uses(changed_root, misses=2)

        # Warm a different project explicitly before making it the active context:
        # the accepted switch must still clear and not inherit that warmed sweep.
        prewarmed, _ = self.find("Different", self.other)
        self.assert_uses(prewarmed, misses=1)
        self.select(self.other, self.alt_root)
        other, _ = self.find("Different")
        self.assert_uses(other, misses=1)
        self.assertEqual(1, other["returnedSiteCount"])
        wrong_context, _ = self.find("NewName")
        self.assertEqual(0, wrong_context["returnedSiteCount"])

        self.select(self.solution, self.alt_root)
        before, _ = self.find("NewName")
        self.assert_uses(before, misses=2)
        self.solution.write_text('<Solution><Project Path="App/App.fsproj" />'
                                 '<Project Path="Other/Other.fsproj" /></Solution>', encoding="utf-8")
        self.select(self.solution, self.alt_root)
        members, _ = self.find("Different")
        self.assert_uses(members, misses=2)
        self.assertEqual(1, members["returnedSiteCount"])
