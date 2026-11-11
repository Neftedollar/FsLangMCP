from __future__ import annotations

import importlib.util
import json
import re
import tempfile
import unittest
import zipfile
from collections.abc import Callable
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


class RuntimeSmokeValidatorTests(unittest.TestCase):
    def setUp(self) -> None:
        temp = tempfile.TemporaryDirectory()
        self.addCleanup(temp.cleanup)
        root = Path(temp.name)
        self.project = root / "Smoke.fsproj"
        self.source = root / "Version.fs"
        self.contextual = {
            "status": "ok",
            "contextMatched": True,
            "activeProjectPath": str(self.project),
            "sessionGeneration": 7,
        }
        self.rebase = {
            "status": "succeeded",
            "verdict": "unknown",
            "scope": "workspace",
            "speed": "fast",
            "via": "fsac",
            "fileGlob": "Version.fs",
            "contextMatched": True,
            "expectationComplete": True,
            "requestedProjectPath": str(self.project),
            "sessionGeneration": 8,
        }
        self.diagnostics = self.rebase | {
            "verdict": "errors",
            "complete": True,
            "analyzed": True,
            "sessionGeneration": 11,
            "expectedFileCount": 1,
            "receivedFileCount": 1,
            "missingFileCount": 0,
            "staleFileCount": 0,
            "analyzedFileCount": 1,
            "expectedFiles": [str(self.source)],
            "receivedFiles": [str(self.source)],
            "missingFiles": [],
            "staleFiles": [],
            "mostRecentAnalyzedAt": "2026-08-14T12:00:00Z",
            "diagnostics": [{"severity": 1, "message": "type mismatch"}],
        }

    def validate_contextual(self, response: object) -> int:
        return runtime_toolchain.require_contextual_response(
            response, self.project, "preload probe"
        )

    def validate_rebase(self, response: object) -> int:
        return runtime_toolchain.require_fsac_rebase(
            response,
            expected_project=self.project,
            expected_glob="Version.fs",
            previous_generation=7,
        )

    def validate_diagnostics(self, response: object) -> None:
        runtime_toolchain.require_fsac_diagnostics(
            response,
            expected_project=self.project,
            expected_file=self.source,
            expected_glob="Version.fs",
            expected_generation=11,
        )

    def assert_field_rejected(
        self,
        validate: Callable[[object], object],
        response: dict,
        field: str,
        invalid_values: tuple[object, ...],
        message: str,
    ) -> None:
        # Change one field at a time so another guard cannot mask a regression.
        for value in invalid_values:
            with self.subTest(validator=validate.__name__, field=field, value=value):
                with self.assertRaisesRegex(RuntimeError, message):
                    validate(response | {field: value})
        missing = response.copy()
        del missing[field]
        with self.subTest(validator=validate.__name__, field=field, missing=True):
            with self.assertRaisesRegex(RuntimeError, message):
                validate(missing)

    def test_accepts_valid_context_rebase_and_error_publication(self) -> None:
        self.assertEqual(7, self.validate_contextual(self.contextual))
        self.assertEqual(8, self.validate_rebase(self.rebase))
        self.assertIsNone(self.validate_diagnostics(self.diagnostics))

        # A rebase may advance more than once; it need not be previous + 1.
        self.assertEqual(
            12, self.validate_rebase(self.rebase | {"sessionGeneration": 12})
        )
        self.assertEqual(
            1, self.validate_contextual(self.contextual | {"sessionGeneration": 1})
        )

    def test_rejects_non_object_payloads(self) -> None:
        for validate in (
            self.validate_contextual,
            self.validate_rebase,
            self.validate_diagnostics,
        ):
            for response in (None, [], "ok", 1, True):
                with self.subTest(validator=validate.__name__, response=response):
                    with self.assertRaises(RuntimeError):
                        validate(response)

    def test_contextual_response_requires_success_status(self) -> None:
        self.assert_field_rejected(
            self.validate_contextual, self.contextual, "status",
            ("error", "succeeded", None, True), "did not succeed",
        )

    def test_context_flags_require_literal_true_independently(self) -> None:
        for validate, response, field, message in (
            (
                self.validate_contextual, self.contextual,
                "contextMatched", "confirm its FSAC context",
            ),
            (
                self.validate_rebase, self.rebase,
                "contextMatched", "escaped the requested context",
            ),
            (
                self.validate_rebase, self.rebase,
                "expectationComplete", "lacked evaluated SourceFiles",
            ),
            (
                self.validate_diagnostics, self.diagnostics,
                "contextMatched", "bound to the active FSAC context",
            ),
            (
                self.validate_diagnostics, self.diagnostics,
                "complete", "coverage is incomplete",
            ),
            (
                self.validate_diagnostics, self.diagnostics,
                "analyzed", "coverage is incomplete",
            ),
        ):
            self.assert_field_rejected(
                validate, response, field, (False, None, 0, 1, "true", [], {}), message,
            )

    def test_rebase_and_diagnostics_require_each_exact_contract_field(self) -> None:
        invalid_fields = {
            "status": "error",
            "verdict": "clean",
            "scope": "file",
            "speed": "full",
            "via": "fcs",
            "fileGlob": "Other.fs",
        }
        for validate, response in (
            (self.validate_rebase, self.rebase),
            (self.validate_diagnostics, self.diagnostics),
        ):
            for field, wrong in invalid_fields.items():
                with self.subTest(validator=validate.__name__):
                    self.assert_field_rejected(
                        validate, response, field, (wrong, None, True, []),
                        "contract mismatch",
                    )

    def test_validators_require_matching_project_paths(self) -> None:
        for validate, response, field, message in (
            (
                self.validate_contextual, self.contextual,
                "activeProjectPath", "wrong active project",
            ),
            (
                self.validate_rebase, self.rebase,
                "requestedProjectPath", "wrong project",
            ),
            (
                self.validate_diagnostics, self.diagnostics,
                "requestedProjectPath", "wrong requested project",
            ),
        ):
            self.assert_field_rejected(
                validate, response, field,
                (str(self.project.with_name("Other.fsproj")), None, "", 1, []), message,
            )

    def test_contextual_generation_must_be_a_positive_integer(self) -> None:
        self.assert_field_rejected(
            self.validate_contextual, self.contextual, "sessionGeneration",
            (0, -1, None, True, False, 7.0, "7", []), "live FSAC session generation",
        )

    def test_rebase_generation_must_advance_and_be_an_integer(self) -> None:
        self.assert_field_rejected(
            self.validate_rebase, self.rebase, "sessionGeneration",
            (7, 6, 0, -1, None, True, 8.0, "8", []), "did not rebase FSAC",
        )

    def test_diagnostics_generation_must_match_and_be_an_integer(self) -> None:
        self.assert_field_rejected(
            self.validate_diagnostics, self.diagnostics, "sessionGeneration",
            (10, 12, 0, -1, None, True, 11.0, "11", []), "active FSAC generation",
        )

    def test_diagnostics_requires_each_coverage_count_independently(self) -> None:
        for field in (
            "expectedFileCount", "receivedFileCount", "missingFileCount",
            "staleFileCount", "analyzedFileCount",
        ):
            expected = self.diagnostics[field]
            self.assert_field_rejected(
                self.validate_diagnostics, self.diagnostics, field,
                (
                    expected + 1, expected - 1, None,
                    True, False, float(expected), str(expected),
                ),
                "coverage counts are invalid",
            )

    def test_diagnostics_requires_exactly_one_expected_and_received_file(self) -> None:
        source = str(self.source)
        for field in ("expectedFiles", "receivedFiles"):
            self.assert_field_rejected(
                self.validate_diagnostics, self.diagnostics, field,
                (
                    None, source, [], [source, source], [1],
                    [str(self.source.with_name("Other.fs"))],
                ),
                "not exactly the expected source file",
            )

    def test_diagnostics_rejects_missing_and_stale_files_independently(self) -> None:
        for field in ("missingFiles", "staleFiles"):
            self.assert_field_rejected(
                self.validate_diagnostics, self.diagnostics, field,
                (None, {}, "", [str(self.source)]), "reported missing or stale files",
            )

    def test_diagnostics_requires_a_nonblank_publication_timestamp(self) -> None:
        self.assert_field_rejected(
            self.validate_diagnostics, self.diagnostics, "mostRecentAnalyzedAt",
            (None, "", " \t\n", 1, True, []), "publication timestamp",
        )

    def test_diagnostics_requires_an_actual_error_diagnostic(self) -> None:
        self.assert_field_rejected(
            self.validate_diagnostics, self.diagnostics, "diagnostics",
            (
                None, {}, [], [None], ["type mismatch"], [{}],
                [{"severity": 2, "message": "type mismatch"}],
                [{"severity": 3}], [{"severity": 4}],
                [{"severity": "1"}], [{"severity": None}],
                [{"severity": True}], [{"severity": 1.0}],
            ),
            "did not surface the deliberate error",
        )

    def test_diagnostics_finds_an_error_among_nonerror_entries(self) -> None:
        response = self.diagnostics | {
            "diagnostics": [None, {}, {"severity": 2}, {"severity": 1}],
        }
        self.assertIsNone(self.validate_diagnostics(response))

    def test_paths_are_compared_after_normalization(self) -> None:
        project = str(self.project.parent / "nested" / ".." / self.project.name)
        source = str(self.source.parent / "nested" / ".." / self.source.name)
        self.assertEqual(
            7,
            self.validate_contextual(self.contextual | {"activeProjectPath": project}),
        )
        self.assertEqual(
            8, self.validate_rebase(self.rebase | {"requestedProjectPath": project})
        )
        self.assertIsNone(self.validate_diagnostics(
            self.diagnostics | {
                "requestedProjectPath": project,
                "expectedFiles": [source],
                "receivedFiles": [source],
            }
        ))


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
    def test_passing_test_evidence_is_retained_as_distinct_trx_only_artifacts(self) -> None:
        def step_block(workflow: str, name: str) -> str:
            match = re.search(
                rf"(?ms)^      - name: {re.escape(name)}\n.*?(?=^      - name:|^  [a-zA-Z0-9_-]+:|\Z)",
                workflow,
            )
            self.assertIsNotNone(match, name)
            return match.group(0)

        ci = (REPOSITORY_ROOT / ".github/workflows/ci.yml").read_text(encoding="utf-8")
        publish = (REPOSITORY_ROOT / ".github/workflows/publish.yml").read_text(encoding="utf-8")
        cases = (
            (ci, "Test (with coverage)", "coverage_tests", "Upload coverage test results",
             "ci-coverage-test-results", "TestResults", "test-results.trx"),
            (ci, "Test with minimum SDK", "minimum_sdk_tests", "Upload minimum SDK test results",
             "minimum-sdk-test-results", "TestResults/minimum-sdk", "minimum-sdk-test-results.trx"),
            (publish, "Test release bits with tag version", "release_bit_tests", "Upload release-bit test results",
             "release-bit-test-results", "TestResults/release-bits", "release-bit-test-results.trx"),
        )
        names = set()
        for workflow, test_name, step_id, upload_name, artifact_name, directory, filename in cases:
            with self.subTest(job=step_id):
                test = step_block(workflow, test_name)
                upload = step_block(workflow, upload_name)
                self.assertIn(f"id: {step_id}", test)
                self.assertIn(f'--logger "trx;LogFileName={filename}"', test)
                self.assertIn(f'--results-directory "$GITHUB_WORKSPACE/{directory}"', test)
                self.assertIn(f"if: always() && steps.{step_id}.outcome != 'skipped'", upload)
                self.assertIn("actions/upload-artifact@cf430e030ddbb5b0abf93d22962f4752f3646cd9", upload)
                self.assertIn(f"name: {artifact_name}", upload)
                self.assertIn(f"path: '${{{{ github.workspace }}}}/{directory}/{filename}'", upload)
                self.assertIn("if-no-files-found: error", upload)
                self.assertIn("retention-days: 30", upload)
                self.assertNotIn("continue-on-error", test + upload)
                self.assertNotIn("--filter", test)
                self.assertNotIn("*", upload)
                self.assertLess(workflow.index(test), workflow.index(upload))
                self.assertNotIn(artifact_name, names)
                names.add(artifact_name)

        self.assertIn("if: failure()", step_block(ci, "Upload test hang diagnostics"))
        self.assertNotIn("nuget-release-package", names)
        release_test = step_block(publish, "Test release bits with tag version")
        for flag in ('-p:Version="${VERSION}"', '-p:PackageVersion="${VERSION}"',
                     '-p:ContinuousIntegrationBuild=true', '--no-build', '--no-restore'):
            self.assertIn(flag, release_test)

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
