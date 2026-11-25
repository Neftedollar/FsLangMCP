"""FCS cache retention and truthful sweep coverage over real MCP stdio."""

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

    def partial_solution(self):
        solution = self.root / "Partial.slnx"
        solution.write_text(
            '<Solution><Project Path="Dep/Dep.fsproj" />'
            '<Project Path="MissingConsumer.fsproj" /></Solution>', encoding="utf-8",
        )
        return solution

    def test_find_pages_match_complete_result_and_edit_requires_restart(self):
        original = self.dep_source.read_text(encoding="utf-8")
        self.dep_source.write_text(
            original + "let first = OldName + 1\nlet second = OldName + 2\n",
            encoding="utf-8",
        )
        args = {
            "projectPath": str(self.dep), "scope": "project", "query": "OldName",
            "kind": "symbol", "exact": True, "includeDeclaration": True,
            "contextLines": 0,
        }

        complete, _ = self.call("find", {**args, "maxResults": 20})
        self.assertIs(complete["coverage"]["complete"], True)
        self.assertIs(complete["resolution"]["complete"], True)
        self.assertIs(complete["resultSetComplete"], True)
        self.assertIs(complete["truncated"], False)
        self.assertIsNone(complete["nextCursor"])
        self.assertEqual(3, complete["totalEstimate"]["sites"])
        self.assertEqual(3, len(complete["sites"]))
        self.assertTrue(all(Path(row["file"]) == self.dep_source for row in complete["sites"]))

        paged = {**args, "maxResults": 1}
        cursor = None
        first_cursor = None
        rows = []
        # Fixed fixture size bounds the traversal: an extra or cyclic cursor fails.
        for offset in range(3):
            request = {**paged, "cursor": cursor} if cursor is not None else paged
            page, _ = self.call("find", request)
            self.assertIs(page["coverage"]["complete"], True)
            self.assertIs(page["resolution"]["complete"], False)
            self.assertIs(page["resultSetComplete"], False)
            self.assertEqual(offset, page["pageOffset"])
            self.assertEqual(3, page["totalEstimate"]["sites"])
            self.assertEqual(1, len(page["sites"]))
            rows.extend(page["sites"])
            cursor = page["nextCursor"]
            self.assertIs(page["truncated"], offset < 2)
            if offset < 2:
                self.assertIsInstance(cursor, str)
                self.assertTrue(cursor)
                if offset == 0:
                    first_cursor = cursor
            else:
                self.assertIsNone(cursor)
        self.assertEqual(complete["sites"], rows)

        with self.dep_source.open("a", encoding="utf-8") as source:
            source.write("let third = OldName + 3\n")
        stale, _ = self.call("find", {**paged, "cursor": first_cursor}, expected="invalid_cursor")
        self.assertEqual("cursor_stale", stale["errorKind"])
        self.assertIs(stale["paginationRestartRequired"], True)
        self.assertIs(stale["retrySameCursor"], False)
        self.assertIsNone(stale["nextCursor"])
        self.assertEqual([], stale["sites"])

        restarted, _ = self.call("find", paged)
        self.assertEqual(0, restarted["pageOffset"])
        self.assertEqual(4, restarted["totalEstimate"]["sites"])
        self.assertEqual(1, len(restarted["sites"]))
        self.assertTrue(restarted["nextCursor"])

    def test_find_partial_preserves_known_site_without_claiming_completeness(self):
        result, _ = self.call("find", {
            "projectPath": str(self.partial_solution()), "scope": "workspace",
            "query": "OldName", "kind": "definition", "exact": True,
        }, expected="partial")
        self.assertIs(result["coverage"]["complete"], False)
        self.assertIs(result["resolution"]["complete"], False)
        self.assertIs(result["resultSetComplete"], False)
        self.assertEqual(2, result["projectsRequested"])
        self.assertEqual(1, result["projectsAnalyzed"])
        self.assertEqual(1, result["projectsMissing"])
        site, = result["sites"]
        self.assertEqual(self.dep_source, Path(site["file"]))
        self.assertEqual("definition", site["kind"])

    def test_dead_code_complete_and_empty_is_a_clean_scan(self):
        payload, _ = self.call("fcs_dead_code", {"projectPath": str(self.app)})
        self.assertTrue(payload["complete"])
        self.assertEqual(1, payload["projectsRequested"])
        self.assertEqual(1, payload["projectsScanned"])
        self.assertEqual([], payload["projectFailures"])
        self.assertEqual([], payload["candidates"])

    def test_dead_code_unloadable_project_is_unknown_not_clean(self):
        broken = self.root / "Broken.fsproj"
        # The path is discovered, but MSBuild cannot evaluate malformed XML.
        broken.write_text("<Project><ItemGroup>", encoding="utf-8")
        payload, _ = self.call("fcs_dead_code", {
            "projectPath": str(broken), "includePublic": True,
        }, expected="unknown")
        self.assertFalse(payload["complete"])
        self.assertEqual(1, payload["projectsRequested"])
        self.assertEqual(0, payload["projectsScanned"])
        self.assertEqual([], payload["candidates"])
        self.assertIn("No F# project was analyzed", payload["verificationHint"])
        failure, = payload["projectFailures"]
        self.assertEqual(broken, Path(failure["projectPath"]))
        self.assertEqual("project_options_failed", failure["errorKind"])
        self.assertIn("Unable to load F# project options", failure["message"])

    def test_dead_code_partial_scan_preserves_provisional_candidates(self):
        missing = self.root / "MissingConsumer.fsproj"
        args = {"projectPath": str(self.partial_solution()), "includePublic": True}
        payload, _ = self.call("fcs_dead_code", args, expected="partial")
        self.assertFalse(payload["complete"])
        self.assertEqual(2, payload["projectsRequested"])
        self.assertEqual(1, payload["projectsScanned"])
        self.assertIn("OldName", [c["name"] for c in payload["candidates"]])
        self.assertIn("provisional", payload["verificationHint"])
        failure, = payload["projectFailures"]
        self.assertEqual(missing, Path(failure["projectPath"]))
        self.assertEqual("project_missing", failure["errorKind"])

        # Even an empty partial page cannot be treated as a clean scan.
        payload, _ = self.call("fcs_dead_code", {**args, "includePublic": False}, expected="partial")
        self.assertFalse(payload["complete"])
        self.assertEqual([], payload["candidates"])

    def test_dead_code_compiler_errors_do_not_establish_clean_coverage(self):
        self.a_source.write_text("module App\nlet bad = NoSuchValue\n", encoding="utf-8")
        payload, _ = self.call("fcs_dead_code", {"projectPath": str(self.app)}, expected="unknown")
        self.assertFalse(payload["complete"])
        self.assertEqual(0, payload["projectsScanned"])
        failure, = payload["projectFailures"]
        self.assertEqual("compiler_error", failure["errorKind"])
        self.assertIn("NoSuchValue", failure["message"])

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
