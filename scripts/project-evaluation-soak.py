#!/usr/bin/env python3
"""Measure genuine project-options reloads in a fresh built/packaged MCP host.

Warm cache hits are controls, never evidence of MSBuild node reclamation. The
gate compares non-ThreadPool thread counts after real invalidations and also
records cold/warm/reload latency. Fixture files are exclusively owned by this run.
"""

from __future__ import annotations

import argparse
import importlib.util
import json
import os
from pathlib import Path
import statistics
import tempfile
import time


def load_client():
    path = Path(__file__).with_name("runtime-toolchain.py")
    spec = importlib.util.spec_from_file_location("evaluation_soak_client", path)
    if spec is None or spec.loader is None:
        raise RuntimeError("cannot load the repository's MCP stdio client")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def validate_samples(samples: list[dict], *, require_helper: bool = True) -> dict:
    warm = [sample for sample in samples if sample["phase"] == "warm"]
    reloads = [sample for sample in samples if sample["phase"] == "reload"]
    if len(warm) < 3 or len(reloads) < 4:
        raise RuntimeError("at least three warm controls and four genuine reloads are required")

    for index, sample in enumerate(samples):
        if require_helper and sample.get("evaluationMode") != "isolated_helper":
            raise RuntimeError("the candidate did not identify isolated helper evaluation")
        if not all(type(sample.get(key)) is int for key in ("processThreads", "threadPoolThreads")):
            raise RuntimeError("thread inspection unavailable; reclamation cannot be proven")
        if sample.get("inFlight") != 0:
            raise RuntimeError("evaluation is still in flight after its trusted check returned")
        if index:
            previous = samples[index - 1]
            increment = 1 if sample["phase"] == "reload" else 0
            if sample["loadAttempts"] != previous["loadAttempts"] + increment:
                raise RuntimeError("sample was not the expected genuine evaluation or cache hit")
            if sample["staleReloads"] != previous["staleReloads"] + increment:
                raise RuntimeError("the changed project fingerprint did not cause exactly one stale reload")

    def non_pool(sample: dict) -> int:
        return sample["processThreads"] - sample["threadPoolThreads"]

    baseline = statistics.median(non_pool(sample) for sample in warm[-3:])
    ending = statistics.median(non_pool(sample) for sample in reloads[-3:])
    # Worker-pool growth is legitimate; small background-runtime variation is
    # also possible. The released defect grows one dedicated thread per reload.
    if ending > baseline + 2:
        raise RuntimeError(f"retained non-ThreadPool threads grew from {baseline} to {ending}")

    return {
        "warmNonThreadPoolMedian": baseline,
        "finalNonThreadPoolMedian": ending,
        "warmSetProjectMedianMs": statistics.median(row["setProjectMs"] for row in warm),
        "reloadSetProjectMedianMs": statistics.median(row["setProjectMs"] for row in reloads),
        "warmCheckMedianMs": statistics.median(row["checkMs"] for row in warm),
        "reloadCheckMedianMs": statistics.median(row["checkMs"] for row in reloads),
    }


def run(args: argparse.Namespace) -> dict:
    if args.iterations < 4:
        raise RuntimeError("--iterations must be at least 4")
    client = load_client()
    dotnet = client.resolve_dotnet(args.dotnet)
    env = client.dotnet_environment(dotnet)
    if args.tool_path:
        env["PATH"] = os.pathsep.join([str(Path(args.tool_path).resolve()), env.get("PATH", "")])
    server = Path(args.server).resolve()
    command = [dotnet, str(server)] if server.suffix.lower() == ".dll" else [str(server)]
    samples: list[dict] = []
    with tempfile.TemporaryDirectory(prefix="fslangmcp_reload_soak_") as directory:
        root = Path(directory)
        project = root / "Soak.fsproj"
        template = '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><DefineConstants>{constant}</DefineConstants></PropertyGroup><ItemGroup><Compile Include="Source.fs" /></ItemGroup></Project>'
        project.write_text(template.format(constant="SOAK_0"), encoding="utf-8")
        (root / "Source.fs").write_text("module Soak\nlet value = 42\n", encoding="utf-8")
        client.run_checked([dotnet, "restore", str(project), "--nologo", "-m:1"], cwd=root, env=env)
        mcp = client.JsonLineProcess(command, cwd=root, env=env)
        request_id = 1

        def invoke(name: str, arguments: dict) -> dict:
            nonlocal request_id
            request_id += 1
            result = client.call_tool(mcp, request_id, name, arguments, 120)
            if not isinstance(result, dict):
                raise RuntimeError(f"{name} did not return an object")
            return result

        def require_definition() -> None:
            found = invoke("find", {"query": "Soak.value", "kind": "definition", "scope": "project", "projectPath": str(project), "includeInfo": False})
            if found.get("resolution", {}).get("fcsSiteCount") != 1 or found.get("totalSites") != 1:
                raise RuntimeError("the fixture definition must have exactly one concrete FCS site")

        def sample(phase: str, iteration: int) -> None:
            started = time.perf_counter()
            selected = invoke("set_project", {"projectPath": str(project), "workspacePath": str(root), "restartLsp": False})
            selected_at = time.perf_counter()
            if selected.get("status") != "ok" or selected.get("result", {}).get("readiness", {}).get("projectOptions") is not True:
                raise RuntimeError("set_project did not prove project-options readiness")
            checked = invoke("check", {"projectPath": str(project), "scope": "project", "timeoutMs": 60000})
            checked_at = time.perf_counter()
            if checked.get("verdict") != "clean" or checked.get("groundTruth") is not True:
                raise RuntimeError("the fixture must receive a trusted clean check")
            status = invoke("fsharp_runtime_status", {"includeFcsCacheStats": True, "includeChildProcesses": False})
            options = status["fcs"]["projectOptions"]
            threads = status["process"]["threads"]
            row = {
                "phase": phase,
                "iteration": iteration,
                "evaluationMode": options.get("evaluationMode"),
                "loadAttempts": options["loadAttempts"],
                "staleReloads": options["staleReloads"],
                "inFlight": options["inFlight"],
                "processThreads": threads.get("process"),
                "threadPoolThreads": threads.get("threadPool"),
                "setProjectMs": round((selected_at - started) * 1000, 2),
                "checkMs": round((checked_at - selected_at) * 1000, 2),
            }
            samples.append(row)
            print(json.dumps(row), flush=True)

        try:
            mcp.send({"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {"protocolVersion": "2024-11-05", "capabilities": {}, "clientInfo": {"name": "project-evaluation-soak", "version": "1"}}})
            initialized = mcp.receive(1, 120)
            if "error" in initialized:
                raise RuntimeError("MCP initialization failed")
            server_info = initialized["result"]["serverInfo"]
            mcp.send({"jsonrpc": "2.0", "method": "notifications/initialized", "params": {}})
            sample("cold", 0)
            require_definition()
            for iteration in range(3):
                sample("warm", iteration)
            for iteration in range(1, args.iterations + 1):
                project.write_text(template.format(constant=f"SOAK_{iteration}"), encoding="utf-8")
                sample("reload", iteration)
            require_definition()
            result = {"serverInfo": server_info, "samples": samples}

            try:
                result.update(validate_samples(samples, require_helper=not args.allow_in_process_baseline))
                result["reclamationPassed"] = True
                return result
            except RuntimeError as error:
                result["reclamationPassed"] = False
                result["failure"] = str(error)
                raise
            finally:
                # Preserve evidence even for the expected red-before baseline.
                if args.report:
                    report = Path(args.report)
                    report.parent.mkdir(parents=True, exist_ok=True)
                    report.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
        finally:
            mcp.close()


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--server", required=True)
    parser.add_argument("--dotnet")
    parser.add_argument("--tool-path")
    parser.add_argument("--iterations", type=int, default=12)
    parser.add_argument("--report")
    parser.add_argument("--allow-in-process-baseline", action="store_true", help="Allow old telemetry solely for red-before controls; thread-growth assertions still apply")
    args = parser.parse_args()
    result = run(args)
    print(json.dumps({"summary": {key: value for key, value in result.items() if key != "samples"}}), flush=True)


if __name__ == "__main__":
    main()
