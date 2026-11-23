"""#225: a real C# project reference behind an F# find request."""

import shutil
import subprocess
import tempfile
import time
import unittest
from pathlib import Path

from scripts.tests.test_sdk_pin_preflight_e2e import StdioClient, locate_server_dll


@unittest.skipUnless(shutil.which("dotnet"), "dotnet is not on PATH")
@unittest.skipUnless(locate_server_dll() is not None, "build the MCP server first")
class ExternalDefinitionEndToEnd(unittest.TestCase):
    def setUp(self):
        temp = tempfile.TemporaryDirectory(prefix="fslangmcp_external_def_")
        self.addCleanup(temp.cleanup)
        # macOS exposes its temp directory through /var -> /private/var.
        # Use one canonical spelling for every build input and server cwd.
        self.root = Path(temp.name).resolve()

        def write(path, contents):
            path = self.root / path
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(contents, encoding="utf-8")
            return path

        properties = "<PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>"
        def project(items=""):
            return f'<Project Sdk="Microsoft.NET.Sdk">{properties}<ItemGroup>{items}</ItemGroup></Project>'

        write("Contracts/Contracts.csproj", project())
        write("Contracts/Contract.cs", "namespace External { public interface IFunctionalGrainTarget {}"
              " public interface AmbiguousExternal {} }"
              " namespace Other { public interface AmbiguousExternal {} }")
        self.consumer = write("Consumer/Consumer.fsproj", project(
            '<Compile Include="Consumer.fs" /><ProjectReference Include="../Contracts/Contracts.csproj" />'
        ))
        write("Consumer/Consumer.fs", "module Consumer\nlet read (x: External.IFunctionalGrainTarget) = x\n"
              "type OnlyLocal = { Value: int }\n"
              "let ambiguous (x: External.AmbiguousExternal) (y: Other.AmbiguousExternal) = x, y\n"
              "let useFrameworkType (value: System.Uri) = value\n")
        self.binary_consumer = write("BinaryConsumer/BinaryConsumer.fsproj", project(
            '<Compile Include="BinaryConsumer.fs" />'
            '<ProjectReference Include="../Contracts/Contracts.csproj" ReferenceOutputAssembly="false" />'
            '<Reference Include="Contracts"><HintPath>../Contracts/obj/Debug/net10.0/ref/Contracts.dll</HintPath></Reference>'
        ))
        write("BinaryConsumer/BinaryConsumer.fs",
              "module BinaryConsumer\nlet read (x: External.IFunctionalGrainTarget) = x\n")
        self.source_project = self.root / "Contracts/Contracts.csproj"
        projects = ["Contracts/Contracts.csproj", "Consumer/Consumer.fsproj"]
        for index in range(1, 8):
            name = f"Leaf{index}"
            projects.append(f"{name}/{name}.fsproj")
            write(projects[-1], project(f'<Compile Include="{name}.fs" />'))
            path = write(f"{name}/{name}.fs", f"module {name}\nlet value = {index}\n")
            if index == 7:
                self.shadow = path

        def solution(members):
            return "<Solution>" + "".join(f'<Project Path="{path}" />' for path in members) + "</Solution>"

        self.solution = write("ExternalDefinition.slnx", solution(projects))
        self.incomplete = write("Incomplete.slnx", solution(projects + ["Missing/Missing.fsproj"]))
        built = subprocess.run(["dotnet", "build", str(self.solution), "-m:1", "-nr:false",
                                "-p:NuGetAudit=false", "-p:UseSharedCompilation=false"],
                               capture_output=True, text=True, timeout=120)
        self.assertEqual(0, built.returncode, built.stdout + built.stderr)

    def call(self, name, args):
        client = StdioClient(locate_server_dll(), self.root)
        try:
            client.request("initialize", {
                "protocolVersion": "2024-11-05", "capabilities": {},
                "clientInfo": {"name": "external-definition-e2e", "version": "1"},
            })
            client.notify("notifications/initialized", {})
            started = time.monotonic()
            result, is_error = client.call_tool(name, args)
            elapsed = round((time.monotonic() - started) * 1000)
            self.assertFalse(is_error, result)
            return result, elapsed
        finally:
            client.close()

    def find(self, query, **extra):
        return self.call("find", {"query": query, "kind": "definition",
                         "projectPath": str(self.solution), "scope": "workspace", **extra})

    def test_external_project_reference_has_no_fake_source_site(self):
        result, elapsed = self.find("IFunctionalGrainTarget")
        resolution = result["resolution"]
        # Measurements are recorded, not used as timing-based assertions.
        print(f"external definition: elapsed={elapsed}ms; "
              f"sweep={result['sweepElapsedMs']}ms; projectsAnalyzed={resolution['projectsAnalyzed']}; "
              f"fallback={resolution['fsacFallbackState']}; outcome={resolution['outcome']}")
        self.assertEqual([], result["sites"])
        self.assertEqual("external_definition", resolution["outcome"])
        self.assertEqual("External.IFunctionalGrainTarget", resolution["externalDefinition"]["fullName"])
        self.assertEqual("Contracts", resolution["externalDefinition"]["assembly"])
        self.assertTrue(resolution["matched"])
        self.assertEqual("not_needed", resolution["fsacFallbackState"])
        suggested = resolution["externalDefinition"]
        self.assertEqual("fcs_referenced_symbols", suggested["suggestedTool"])
        self.assertEqual(self.consumer.resolve(), Path(suggested["projectPath"]).resolve())
        self.assertEqual("csharp", suggested["sourceProjectLanguage"])
        self.assertEqual(self.source_project.resolve(), Path(suggested["sourceProjectPath"]).resolve())
        referenced, _ = self.call(suggested["suggestedTool"], {
            "projectPath": suggested["projectPath"], "query": suggested["fullName"],
        })
        self.assertTrue(any(item["fullName"] == suggested["fullName"] and
                            item["assembly"] == suggested["assembly"]
                            for item in referenced["results"]), referenced)

    def test_binary_and_framework_metadata_do_not_claim_csharp_project(self):
        restored = subprocess.run(["dotnet", "restore", str(self.binary_consumer), "-m:1",
                                   "-nr:false", "-p:NuGetAudit=false"],
                                  capture_output=True, text=True, timeout=120)
        self.assertEqual(0, restored.returncode, restored.stdout + restored.stderr)
        binary, _ = self.find("IFunctionalGrainTarget", projectPath=str(self.binary_consumer), scope="project")
        self.assertEqual("external_definition", binary["resolution"]["outcome"])
        identity = binary["resolution"]["externalDefinition"]
        self.assertEqual("External.IFunctionalGrainTarget", identity["fullName"])
        self.assertEqual("Contracts", identity["assembly"])
        self.assertNotIn("sourceProjectLanguage", identity)
        self.assertNotIn("sourceProjectPath", identity)

        framework, _ = self.find("System.Uri", projectPath=str(self.consumer), scope="project")
        self.assertEqual("external_definition", framework["resolution"]["outcome"])
        identity = framework["resolution"]["externalDefinition"]
        self.assertEqual("System.Uri", identity["fullName"])
        self.assertNotIn("sourceProjectLanguage", identity)
        self.assertNotIn("sourceProjectPath", identity)

    def test_local_shadow_and_true_miss_keep_normal_behavior(self):
        self.shadow.write_text("module Leaf7\ntype IFunctionalGrainTarget = { Local: int }\n", encoding="utf-8")
        shadow, _ = self.find("IFunctionalGrainTarget")
        self.assertTrue(any(site["kind"] == "definition" and site["file"].endswith("Leaf7.fs")
                            for site in shadow["sites"]), shadow["resolution"])
        local, _ = self.find("OnlyLocal")
        self.assertEqual("matched", local["resolution"]["outcome"])
        self.assertGreater(len(local["sites"]), 0)
        miss, _ = self.find("MissingExternalDefinitionFixtureSymbol")
        self.assertEqual("not_found", miss["resolution"]["outcome"])

    def test_ambiguity_and_incomplete_coverage_do_not_claim_external_identity(self):
        ambiguous, _ = self.find("AmbiguousExternal")
        self.assertEqual("not_found", ambiguous["resolution"]["outcome"])
        self.assertNotIn("externalDefinition", ambiguous["resolution"])
        incomplete, _ = self.find("IFunctionalGrainTarget", projectPath=str(self.incomplete))
        self.assertEqual("indeterminate", incomplete["resolution"]["outcome"])
        self.assertNotIn("externalDefinition", incomplete["resolution"])

        # A CLR type can be available in the compiler reference graph without
        # appearing in any analyzed F# use site. Metadata alone is not evidence.
        referenced, _ = self.call("fcs_referenced_symbols", {
            "projectPath": str(self.consumer), "query": "System.Guid",
        })
        self.assertTrue(any(item["fullName"] == "System.Guid" for item in referenced["results"]))
        unobserved, _ = self.find("System.Guid", projectPath=str(self.consumer), scope="project")
        self.assertEqual("not_found", unobserved["resolution"]["outcome"])
        self.assertNotIn("externalDefinition", unobserved["resolution"])

        self.shadow.write_text("module Leaf7\nlet missing = undefinedFixtureValue\n", encoding="utf-8")
        erroneous, _ = self.find("IFunctionalGrainTarget")
        self.assertGreater(erroneous["projectDiagnosticsTotalCount"], 0)
        self.assertNotIn("externalDefinition", erroneous["resolution"])


if __name__ == "__main__":
    unittest.main()
