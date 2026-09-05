from __future__ import annotations

import copy
import importlib.util
from pathlib import Path
import unittest


script = Path(__file__).resolve().parents[1] / "project-evaluation-soak.py"
spec = importlib.util.spec_from_file_location("project_evaluation_soak", script)
assert spec is not None and spec.loader is not None
soak = importlib.util.module_from_spec(spec)
spec.loader.exec_module(soak)


class EvaluationSoakContractTests(unittest.TestCase):
    def samples(self) -> list[dict]:
        result = []
        for index in range(12):
            reload = max(0, index - 3)
            pool = 7 + index // 3
            result.append({
                "phase": "cold" if index == 0 else "warm" if index <= 3 else "reload",
                "evaluationMode": "isolated_helper",
                "loadAttempts": 1 + reload,
                "staleReloads": reload,
                "inFlight": 0,
                "processThreads": 15 + pool,
                "threadPoolThreads": pool,
                "setProjectMs": 100,
                "checkMs": 20,
            })
        return result

    def test_legitimate_worker_pool_growth_does_not_look_like_msbuild_retention(self):
        result = soak.validate_samples(self.samples())
        self.assertEqual(15, result["warmNonThreadPoolMedian"])
        self.assertEqual(15, result["finalNonThreadPoolMedian"])

    def test_warm_cache_hits_cannot_pass_as_reclamation_evidence(self):
        rows = self.samples()
        for row in rows:
            row["loadAttempts"] = 1
            row["staleReloads"] = 0
        with self.assertRaisesRegex(RuntimeError, "genuine evaluation"):
            soak.validate_samples(rows)

    def test_released_linear_dedicated_thread_growth_fails(self):
        rows = self.samples()
        for row in rows:
            row["processThreads"] += row["staleReloads"]
        with self.assertRaisesRegex(RuntimeError, "retained non-ThreadPool"):
            soak.validate_samples(rows)

    def test_missing_inspection_live_workers_and_wrong_mode_fail_closed(self):
        for key, replacement in (("processThreads", None), ("threadPoolThreads", None), ("inFlight", 1), ("evaluationMode", "in_process")):
            with self.subTest(key=key), self.assertRaises(RuntimeError):
                rows = copy.deepcopy(self.samples())
                rows[-1][key] = replacement
                soak.validate_samples(rows)

    def test_baseline_mode_does_not_disable_the_thread_growth_gate(self):
        rows = self.samples()
        for row in rows:
            row["evaluationMode"] = None
            row["processThreads"] += row["staleReloads"]
        with self.assertRaisesRegex(RuntimeError, "retained non-ThreadPool"):
            soak.validate_samples(rows, require_helper=False)

    def test_extra_warm_loads_and_missing_stale_accounting_fail(self):
        for index, key in ((2, "loadAttempts"), (7, "staleReloads")):
            with self.subTest(key=key), self.assertRaises(RuntimeError):
                rows = self.samples()
                rows[index][key] += 1
                soak.validate_samples(rows)

    def test_too_few_controls_cannot_prove_a_plateau(self):
        with self.assertRaisesRegex(RuntimeError, "three warm controls"):
            soak.validate_samples(self.samples()[:5])


if __name__ == "__main__":
    unittest.main()
