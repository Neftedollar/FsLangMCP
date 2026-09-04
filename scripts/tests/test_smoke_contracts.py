from __future__ import annotations

import importlib.util
import json
import re
import tempfile
import unittest
import zipfile
from pathlib import Path


REPOSITORY_ROOT = Path(__file__).resolve().parents[2]


def load_script(module_name: str, relative_path: str):
    path = REPOSITORY_ROOT / relative_path
    spec = importlib.util.spec_from_file_location(module_name, path)
    if spec is None or spec.loader is None:
        raise RuntimeError(f"cannot load {path}")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


runtime_toolchain = load_script(
    "runtime_toolchain_smoke_contract", "scripts/runtime-toolchain.py"
)
package_verifier = load_script(
    "package_version_smoke_contract", "scripts/verify-package-version.py"
)


class RuntimeSmokeContractTests(unittest.TestCase):
    def test_live_sequence_proves_preload_then_fsac_diagnostics(self) -> None:
        smoke_script = (
            REPOSITORY_ROOT / "scripts/runtime-toolchain.py"
        ).read_text(encoding="utf-8")

        launch = smoke_script.index(
            '[str(server), "--project", str(smoke_project)]'
        )
        preload = smoke_script.index("preload_rename = invoke(", launch)
        explicit_set_project = smoke_script.index("set_project = invoke(", preload)
        disk_error = smoke_script.index(
            "smoke_file.write_text(broken_source", explicit_set_project
        )
        diagnostics_rebase = smoke_script.index(
            "diagnostics_rebase = invoke(", disk_error
        )
        rebase_validation = smoke_script.index(
            "diagnostics_generation = require_fsac_rebase(", diagnostics_rebase
        )
        diagnostics_sync = smoke_script.index(
            "diagnostics_sync = invoke(", rebase_validation
        )
        diagnostics_after_sync_rebase = smoke_script.index(
            "diagnostics_after_sync_rebase = invoke(", diagnostics_sync
        )
        after_sync_rebase_validation = smoke_script.index(
            "diagnostics_generation = require_fsac_rebase(",
            diagnostics_after_sync_rebase,
        )
        diagnostics_baseline_reopen = smoke_script.index(
            "diagnostics_baseline_reopen = invoke(",
            after_sync_rebase_validation,
        )
        baseline_reopen_validation = smoke_script.index(
            "reopened_generation = require_contextual_response(",
            diagnostics_baseline_reopen,
        )
        fast_check = smoke_script.index(
            'diagnostics = invoke(\n                    "check",',
            baseline_reopen_validation,
        )
        diagnostics_validation = smoke_script.index(
            "require_fsac_diagnostics(", fast_check
        )

        self.assertLess(preload, explicit_set_project)
        self.assertLess(explicit_set_project, disk_error)
        self.assertLess(disk_error, diagnostics_rebase)
        self.assertLess(diagnostics_rebase, rebase_validation)
        self.assertLess(rebase_validation, diagnostics_sync)
        self.assertLess(diagnostics_sync, diagnostics_after_sync_rebase)
        self.assertLess(
            diagnostics_after_sync_rebase, after_sync_rebase_validation
        )
        self.assertLess(after_sync_rebase_validation, diagnostics_baseline_reopen)
        self.assertLess(diagnostics_baseline_reopen, baseline_reopen_validation)
        self.assertLess(baseline_reopen_validation, fast_check)
        self.assertLess(fast_check, diagnostics_validation)

        tainted_sync_contract = smoke_script[
            diagnostics_sync:diagnostics_after_sync_rebase
        ]
        self.assertIn('"text": tainted_source', tainted_sync_contract)

        baseline_reopen_contract = smoke_script[
            diagnostics_baseline_reopen:fast_check
        ]
        self.assertNotIn('"text":', baseline_reopen_contract)

        fast_check_contract = smoke_script[fast_check:diagnostics_validation]
        for field in (
            '"scope": "workspace"',
            '"fileGlob": diagnostics_glob',
            '"speed": "fast"',
            '"severity": "all"',
        ):
            with self.subTest(field=field):
                self.assertIn(field, fast_check_contract)
        self.assertNotIn('"snippet"', fast_check_contract)

    def test_cli_preload_initialize_uses_the_configured_live_budget(self) -> None:
        smoke_script = (
            REPOSITORY_ROOT / "scripts/runtime-toolchain.py"
        ).read_text(encoding="utf-8")

        launch = smoke_script.index(
            '[str(server), "--project", str(smoke_project)]'
        )
        initialize = smoke_script.index(
            "initialize = mcp.receive(1, args.timeout_seconds * 4.0)", launch
        )
        first_tool = smoke_script.index("preload_rename = invoke(", initialize)

        self.assertLess(initialize, first_tool)
        self.assertNotIn("initialize = mcp.receive(1, 30)", smoke_script)
        self.assertIn(
            'child_env["FSLANGMCP_PROJ_INFO_TIMEOUT_MS"] = timeout_ms',
            smoke_script,
        )

    def test_diagnostics_rebase_requires_same_context_and_new_generation(self) -> None:
        with tempfile.TemporaryDirectory() as temp_name:
            project = Path(temp_name) / "Smoke.fsproj"
            response = {
                "status": "succeeded",
                "verdict": "unknown",
                "scope": "workspace",
                "speed": "fast",
                "via": "fsac",
                "fileGlob": "Version.fs",
                "contextMatched": True,
                "expectationComplete": True,
                "requestedProjectPath": str(project),
                "sessionGeneration": 8,
            }

            self.assertEqual(
                8,
                runtime_toolchain.require_fsac_rebase(
                    response,
                    expected_project=project,
                    expected_glob="Version.fs",
                    previous_generation=7,
                ),
            )

            for invalid in (
                response | {"contextMatched": False},
                response | {"sessionGeneration": 7},
                response | {"requestedProjectPath": str(project.parent / "Other.fsproj")},
            ):
                with self.subTest(response=invalid), self.assertRaises(RuntimeError):
                    runtime_toolchain.require_fsac_rebase(
                        invalid,
                        expected_project=project,
                        expected_glob="Version.fs",
                        previous_generation=7,
                    )

    def test_cli_preload_requires_matching_context_and_generation(self) -> None:
        with tempfile.TemporaryDirectory() as temp_name:
            project = Path(temp_name) / "Smoke.fsproj"
            response = {
                "status": "ok",
                "contextMatched": True,
                "activeProjectPath": str(project),
                "sessionGeneration": 7,
            }

            self.assertEqual(
                7,
                runtime_toolchain.require_contextual_response(
                    response, project, "preload probe"
                ),
            )

            wrong_context = response | {
                "activeProjectPath": str(project.parent / "Other.fsproj")
            }
            with self.assertRaisesRegex(RuntimeError, "wrong active project"):
                runtime_toolchain.require_contextual_response(
                    wrong_context, project, "preload probe"
                )

    def test_fast_diagnostics_requires_fsac_context_glob_and_generation(self) -> None:
        with tempfile.TemporaryDirectory() as temp_name:
            root = Path(temp_name)
            project = root / "Smoke.fsproj"
            source = root / "Version.fs"
            response = {
                "status": "succeeded",
                "verdict": "errors",
                "scope": "workspace",
                "speed": "fast",
                "via": "fsac",
                "fileGlob": "Version.fs",
                "contextMatched": True,
                "complete": True,
                "analyzed": True,
                "requestedProjectPath": str(project),
                "sessionGeneration": 11,
                "expectedFileCount": 1,
                "receivedFileCount": 1,
                "missingFileCount": 0,
                "staleFileCount": 0,
                "analyzedFileCount": 1,
                "expectedFiles": [str(source)],
                "receivedFiles": [str(source)],
                "missingFiles": [],
                "staleFiles": [],
                "mostRecentAnalyzedAt": "2026-08-14T12:00:00Z",
                "diagnostics": [{"severity": 1, "message": "type mismatch"}],
            }

            runtime_toolchain.require_fsac_diagnostics(
                response,
                expected_project=project,
                expected_file=source,
                expected_glob="Version.fs",
                expected_generation=11,
            )

            invalid_cases = {
                "trusted FCS path": response | {"via": "fcs"},
                "missing fileGlob binding": response | {"fileGlob": None},
                "wrong generation": response | {"sessionGeneration": 12},
                "stale publication": response
                | {
                    "complete": False,
                    "analyzed": False,
                    "staleFileCount": 1,
                    "staleFiles": [str(source)],
                },
            }
            for name, invalid in invalid_cases.items():
                with self.subTest(name=name), self.assertRaises(RuntimeError):
                    runtime_toolchain.require_fsac_diagnostics(
                        invalid,
                        expected_project=project,
                        expected_file=source,
                        expected_glob="Version.fs",
                        expected_generation=11,
                    )


class PackageVersionContractTests(unittest.TestCase):
    def test_packaged_runtime_must_be_present_and_nonempty(self) -> None:
        with tempfile.TemporaryDirectory() as temp_name:
            root = Path(temp_name)
            valid = root / "valid.nupkg"
            empty = root / "empty.nupkg"

            with zipfile.ZipFile(valid, "w") as archive:
                archive.writestr("tools/net10.0/any/FsLangMcp.dll", b"runtime")
            with zipfile.ZipFile(empty, "w") as archive:
                archive.writestr("tools/net10.0/any/FsLangMcp.dll", b"")

            self.assertEqual(7, package_verifier.packaged_runtime_size(valid))
            with self.assertRaisesRegex(RuntimeError, "FsLangMcp.dll is empty"):
                package_verifier.packaged_runtime_size(empty)

    def test_initialize_server_info_must_match_release_version(self) -> None:
        initialize = {
            "jsonrpc": "2.0",
            "id": 1,
            "result": {
                "serverInfo": {"name": "fsharp-fsautocomplete", "version": "0.14.0"}
            },
        }

        self.assertEqual(
            "0.14.0",
            package_verifier.verify_initialize_server_version(initialize, "0.14.0"),
        )

        for invalid in (
            initialize
            | {
                "result": {
                    "serverInfo": {
                        "name": "fsharp-fsautocomplete",
                        "version": "0.14.0.0",
                    }
                }
            },
            {"jsonrpc": "2.0", "id": 1, "result": {}},
        ):
            with self.subTest(initialize=invalid), self.assertRaisesRegex(
                RuntimeError, "initialize.serverInfo.version mismatch"
            ):
                package_verifier.verify_initialize_server_version(invalid, "0.14.0")


class WorkflowGateContractTests(unittest.TestCase):
    def test_repository_sdk_floor_is_flexible_but_release_jobs_stay_pinned(self) -> None:
        def job_block(workflow: str, job_name: str) -> str:
            marker = f"  {job_name}:\n"
            start = workflow.index(marker)
            remainder = workflow[start + len(marker) :]
            next_job = re.search(r"(?m)^  [A-Za-z0-9_-]+:\s*$", remainder)
            end = start + len(marker) + (next_job.start() if next_job else len(remainder))
            return workflow[start:end]

        global_json = json.loads(
            (REPOSITORY_ROOT / "global.json").read_text(encoding="utf-8")
        )
        sdk = global_json["sdk"]

        self.assertEqual("10.0.100", sdk["version"])
        self.assertEqual("latestFeature", sdk["rollForward"])
        self.assertIs(sdk["allowPrerelease"], False)

        ci_workflow = (REPOSITORY_ROOT / ".github/workflows/ci.yml").read_text(
            encoding="utf-8"
        )
        minimum_job = job_block(ci_workflow, "minimum-sdk")
        self.assertIn(
            "DOTNET_INSTALL_DIR: ${{ github.workspace }}/.dotnet-minimum", minimum_job
        )
        self.assertIn("DOTNET_MULTILEVEL_LOOKUP: '0'", minimum_job)
        self.assertIn("dotnet-version: '10.0.100'", minimum_job)
        self.assertIn('test "$(dotnet --version)" = "10.0.100"', minimum_job)
        self.assertIn("dotnet restore FsLangMcp.slnx --locked-mode", minimum_job)
        self.assertIn("dotnet build FsLangMcp.slnx", minimum_job)
        self.assertIn("dotnet test FsLangMcp.slnx", minimum_job)

        live_workflow = (
            REPOSITORY_ROOT / ".github/workflows/live-fsac.yml"
        ).read_text(encoding="utf-8")
        publish_workflow = (
            REPOSITORY_ROOT / ".github/workflows/publish.yml"
        ).read_text(encoding="utf-8")
        exact_jobs = {
            "primary": (
                job_block(ci_workflow, "build-and-test"),
                ('test "$(dotnet --version)" = "10.0.400"',),
            ),
            "live-fsac": (
                job_block(live_workflow, "live-fsac"),
                (
                    '$actual -ne "10.0.400"',
                    'throw "Expected .NET SDK 10.0.400, got $actual"',
                ),
            ),
            "publish": (
                job_block(publish_workflow, "build-and-verify"),
                ('test "$(dotnet --version)" = "10.0.400"',),
            ),
        }

        for name, (exact_job, guards) in exact_jobs.items():
            with self.subTest(job=name):
                self.assertIn(
                    "DOTNET_INSTALL_DIR: ${{ github.workspace }}/.dotnet-exact",
                    exact_job,
                )
                self.assertIn("DOTNET_MULTILEVEL_LOOKUP: '0'", exact_job)
                self.assertRegex(
                    exact_job, r"dotnet-version:\s+['\"]10\.0\.400['\"]"
                )
                for guard in guards:
                    self.assertIn(guard, exact_job)

    def test_publish_waits_for_same_commit_reusable_live_matrix(self) -> None:
        live_workflow = (REPOSITORY_ROOT / ".github/workflows/live-fsac.yml").read_text(
            encoding="utf-8"
        )
        publish_workflow = (REPOSITORY_ROOT / ".github/workflows/publish.yml").read_text(
            encoding="utf-8"
        )

        self.assertRegex(live_workflow, r"(?m)^  workflow_call:\s*$")
        self.assertIn(
            "  live-fsac:\n"
            "    permissions:\n"
            "      contents: read\n"
            "    uses: ./.github/workflows/live-fsac.yml\n",
            publish_workflow,
        )
        self.assertRegex(
            publish_workflow,
            r"(?m)^    needs: \[build-and-verify, live-fsac\]\s*$",
        )

        # A detached workflow_run can accidentally approve another SHA/run. The local
        # reusable call above is the only accepted coupling for this release gate.
        self.assertNotRegex(publish_workflow, r"(?m)^\s*workflow_run:\s*$")

    def test_live_matrix_uses_packaged_tool_and_proves_global_bootstrap(self) -> None:
        live_workflow = (REPOSITORY_ROOT / ".github/workflows/live-fsac.yml").read_text(
            encoding="utf-8"
        )

        required_contracts = (
            'if ($env:GITHUB_REF_TYPE -eq "tag")',
            '$version = $env:GITHUB_REF_NAME.Substring(1)',
            '"FSLANGMCP_LIVE_VERSION=$version"',
            '-p:Version="$env:FSLANGMCP_LIVE_VERSION"',
            '-p:PackageVersion="$env:FSLANGMCP_LIVE_VERSION"',
            'dotnet tool install `\n'
            '            --tool-path "$installedToolDirectory"',
            '& "${{ env.FSLANGMCP_LIVE_SERVER }}" --bootstrap-tools',
            "$env:DOTNET_CLI_HOME = $bootstrapHome",
            'Push-Location $workDirectory',
            '$fantomasVersion = (dotnet fantomas --version).Trim()',
            'dotnet fantomas "Smoke.fs"',
            '--server "${{ env.FSLANGMCP_LIVE_SERVER }}"',
        )
        for contract in required_contracts:
            with self.subTest(contract=contract):
                self.assertIn(contract, live_workflow)

        self.assertNotIn("--server bin/Release/", live_workflow)

    def test_bootstrap_smoke_project_is_not_an_indented_powershell_here_string(
        self,
    ) -> None:
        live_workflow = (REPOSITORY_ROOT / ".github/workflows/live-fsac.yml").read_text(
            encoding="utf-8"
        )

        bootstrap_step = live_workflow.index(
            "- name: Prove the packaged bootstrap contract outside the local manifest"
        )
        next_step = live_workflow.index(
            "- name: Install exact runtime toolchain", bootstrap_step
        )
        bootstrap_contract = live_workflow[bootstrap_step:next_step]

        self.assertNotIn('@"', bootstrap_contract)
        self.assertNotIn('"@', bootstrap_contract)
        self.assertRegex(bootstrap_contract, r"(?m)^\s+@\(\s*$")
        self.assertIn(
            "'<Project Sdk=\"Microsoft.NET.Sdk\">'",
            bootstrap_contract,
        )
        self.assertIn(
            ') | Set-Content -Path "Smoke.fsproj" -Encoding utf8',
            bootstrap_contract,
        )

    def test_publish_smoke_checks_all_three_runtime_version_surfaces(self) -> None:
        publish_workflow = (REPOSITORY_ROOT / ".github/workflows/publish.yml").read_text(
            encoding="utf-8"
        )

        install = publish_workflow.index("dotnet tool install")
        cli_version = publish_workflow.index("INSTALLED_VERSION=", install)
        verifier = publish_workflow.index(
            "python3 scripts/verify-package-version.py", cli_version
        )
        upload = publish_workflow.index(
            "- name: Upload the single verified release artifact", verifier
        )

        self.assertLess(install, cli_version)
        self.assertLess(cli_version, verifier)
        self.assertLess(verifier, upload)
        self.assertIn('--installed-tool "${INSTALLED_TOOL}"', publish_workflow)


if __name__ == "__main__":
    unittest.main()
