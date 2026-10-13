"""Run synthetic server stress cases through the open editor MCP; retain raw results."""
import argparse
import datetime as dt
import hashlib
import json
import re
from pathlib import Path
import shutil
import subprocess
import time
import urllib.request
import uuid

ROOT = Path(__file__).resolve().parent
ENGINE = ROOT.parents[3]
LIBRARY = Path("D:/Sandbox.Public.Projects")
DATA = ENGINE / "game/data/local/networkobjectload#local/network-object-load"


def call(name, arguments=None):
    request = urllib.request.Request("http://127.0.0.1:7269/mcp", data=json.dumps({
        "jsonrpc": "2.0", "id": 1, "method": "tools/call", "params": {
            "name": name, "arguments": arguments or {}}}).encode(),
        headers={"Content-Type": "application/json", "Accept": "application/json, text/event-stream"})
    response = json.load(urllib.request.urlopen(request, timeout=45))
    if "error" in response:
        raise RuntimeError(response["error"])
    result = response["result"]
    if result.get("isError"):
        raise RuntimeError(result)
    if "structuredContent" in result:
        return result["structuredContent"]
    text = "\n".join(c.get("text", "") for c in result.get("content", []))
    try:
        return json.loads(text)
    except json.JSONDecodeError:
        return text


def tool(name, **arguments):
    return call("call_tool", {"name": name, "arguments": arguments})


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def environment():
    hardware = subprocess.check_output(["powershell", "-NoProfile", "-Command",
        "@{cpu=(Get-CimInstance Win32_Processor | Select-Object Name); gpu=(Get-CimInstance Win32_VideoController | Select-Object Name,DriverVersion); os=(Get-CimInstance Win32_OperatingSystem | Select-Object Caption,Version)} | ConvertTo-Json -Depth 4"], text=True)
    return {"utc": dt.datetime.now(dt.timezone.utc).isoformat(),
        "commit": subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ENGINE, text=True).strip(),
        "diffSha256": hashlib.sha256(subprocess.check_output(["git", "diff", "HEAD"], cwd=ENGINE)).hexdigest(),
        "sourceSha256": {p.name: sha(p) for p in (ROOT / "code").glob("*.cs")},
        "managedSha256": {n: sha(ENGINE / "game/bin/managed" / n) for n in ["Sandbox.Engine.dll", "Sandbox.System.dll", "Sandbox.GameInstance.dll"]},
        "nativeSha256": {n: sha(ENGINE / "game/bin/win64" / n) for n in ["engine2.dll", "rendersystemvulkan.dll"]},
        "hardware": json.loads(hardware), "mode": "editor host, fake connections, no client processes",
        "profiled": False, "renderSubjects": False,
        "limitations": ["Real transport/client correctness excluded", "Editor and renderer contribute to frame/alloc counters", "Resolution/quality/vsync/frame-cap not fully recorded; provisional baseline"]}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--batch", type=Path)
    parser.add_argument("--profile-dir", type=Path, help="Directory used by the pre-approved elevated capture helper")
    parser.add_argument("--profile-case", nargs=3, metavar=("SCENARIO", "POPULATION", "CONNECTIONS"),
                        help="Explicit diagnostic workload; permits an empty unprofiled plan")
    parser.add_argument("--smoke", action="store_true")
    parser.add_argument("--plan", type=Path, help="JSON array of [scenario, population, fake connections] cases")
    parser.add_argument("--resume", action="store_true", help="Append a recovery plan to an existing batch")
    parser.add_argument("--heavy-repeats", type=int, default=1, help="Explicit repeats of the default four-case matrix")
    parser.add_argument("--frame-cap", type=int, default=90, help="Identical active/inactive cap for controlled comparisons")
    args = parser.parse_args()
    if not 1 <= args.heavy_repeats <= 10:
        parser.error("heavy-repeats must be 1..10")
    if not 1 <= args.frame_cap <= 1000:
        parser.error("frame-cap must be 1..1000")
    status = call("editor_status")
    if status["Project"] != "networkobjectload" or status["IsPlaying"] or status["IsCompiling"] or not status["LastCompileSucceeded"]:
        raise RuntimeError("Open the compiled Network Object Load project and stop Play first.")
    if any(s["HasUnsavedChanges"] for s in tool("list_scenes")["Scenes"]):
        raise RuntimeError("Save existing scene changes before running; the runner only discards its own temporary settings.")
    original_scene = status["ActiveScenePath"] or "scenes/18-fake-32.scene"
    convars = json.loads((ENGINE / "game/core/cfg/machine_convars.json").read_text())["convars"]
    original_cap = int(convars.get("fps_max", "0"))
    original_inactive_cap = int(convars.get("fps_max_inactive", "100"))
    scene = "scenes/18-fake-32.scene"
    batch = args.batch or LIBRARY / "results/network-object-load" / ("batch-" + dt.datetime.now(dt.timezone.utc).strftime("%Y%m%d-%H%M%S"))
    batch.mkdir(parents=True, exist_ok=True)
    meta = environment()
    meta.update(benchmarkFrameCap=args.frame_cap, inactiveFrameCap=args.frame_cap, originalFrameCap=original_cap, originalInactiveFrameCap=original_inactive_cap)
    meta["limitations"][-1] = "Resolution/quality/vsync not fully recorded; editor-host baseline"
    if args.resume:
        previous = json.loads((batch / "environment.json").read_text())
        for key in ("sourceSha256", "managedSha256", "nativeSha256", "benchmarkFrameCap", "inactiveFrameCap"):
            if previous[key] != meta[key]:
                raise RuntimeError(f"Cannot resume with changed {key}")
        (batch / "recovery-environment.json").write_text(json.dumps(meta, indent=2))
    else:
        (batch / "environment.json").write_text(json.dumps(meta, indent=2))
    plan = [("IdleSynced", 10000, 32), ("Changing", 1000, 32),
            ("IdleSynced", 100, 32), ("Changing", 100, 32)]
    if args.smoke:
        plan = [("IdleSynced", 100, 8)]
    else:
        plan *= args.heavy_repeats
    if args.plan:
        plan = json.loads(args.plan.read_text())
    (batch / ("recovery-plan.json" if args.resume else "plan.json")).write_text(json.dumps(plan, indent=2))
    print(f"Results: {batch}; {len(plan)} unprofiled runs", flush=True)
    records = json.loads((batch / "results.json").read_text()) if args.resume else []
    first_index = len(records) + 1
    edited = False
    skip_large = False

    def run(index, case, population, connections, profiled=False):
        nonlocal edited
        # Reopen from disk each time; no benchmark configuration is saved over the scenes.
        if edited:
            tool("close_scene", scene=scene, discardChanges=True)
            edited = False
        tool("open_scene", path=scene)
        tree = tool("scene_tree", maxDepth=1)
        controller = next(x for x in tree["Objects"] if "NetworkObjectScenario" in x["Components"])
        label = f"{index:02}-{case}-{population}-{connections}-{uuid.uuid4().hex[:8]}"
        props = {"Scenario": case, "Population": population, "FakeConnections": connections,
                 "MinimumClients": 0, "WarmupTicks": 500, "RunTicks": 1500,
                 "TimeoutSeconds": 180, "SettleSeconds": 5, "RenderSubjects": False,
                 "PropertyGroups": 1, "ActiveFraction": 0.1, "RpcsPerTick": 1 if case == "Mixed" else 0,
                 "CaptureMetrics": True, "RunLabel": label, "ProfiledRun": profiled}
        if args.smoke:
            props.update(WarmupTicks=50, RunTicks=100)
        tool("set_component", id=controller["Id"], type="NetworkObjectScenario", properties=props)
        edited = True
        print(f"RUN {index}: {case}, {population} objects, {connections} fake, profiled={profiled}", flush=True)
        cursor = 0
        if profiled:
            before = call("read_console", {"limit": 1})
            cursor = int(re.search(r"Cursor: (\d+)", before).group(1))
        tool("play_start")
        began = time.monotonic()
        triggered = False
        while time.monotonic() - began < 420:
            if profiled and not triggered:
                console = call("read_console", {"filter": "NETWORK LOAD", "limit": 5, "since": cursor})
                if f"{case} / Running:" in console:
                    args.profile_dir.mkdir(parents=True, exist_ok=True)
                    (args.profile_dir / "trigger.json").write_text(json.dumps({"label": label}))
                    triggered = True
            candidates = list(DATA.glob(label + "-*/summary.json")) if DATA.exists() else []
            if candidates:
                source = candidates[0].parent
                # The exporter writes the manifest after the summary; wait for the whole export.
                if not (source / "manifest.json").exists():
                    time.sleep(0.1)
                    continue
                summary = json.loads(candidates[0].read_text())
                destination = batch / label
                shutil.copytree(source, destination)
                (destination / "environment.json").write_text((batch / "environment.json").read_text())
                log = call("read_console", {"filter": "NETWORK", "limit": 100})
                (destination / "console.log").write_text(log)
                tool("play_stop")
                summary.update(folder=label, profiled=profiled)
                records.append(summary)
                (batch / "results.json").write_text(json.dumps(records, indent=2))
                print(f"DONE {index}: valid={summary['valid']}; mean={summary['meanMs']:.3f}ms; p99={summary['p99Ms']:.3f}ms; network={summary['networkMeanMs']:.3f}ms; alloc={summary['meanAllocationBytes']:.0f}B/frame; GC={summary['gcPauseMs']:.2f}ms", flush=True)
                if not summary["valid"] and summary["reason"] != "Workload wall-clock timeout":
                    raise RuntimeError(f"Invalid run preserved: {destination}: {summary['reason']}")
                return summary
            time.sleep(1)
        (batch / "timeout-console.log").write_text(call("read_console", {"limit": 100}))
        raise TimeoutError(f"No completed export for {label}")

    try:
        tool("console_command", command=f"fps_max {args.frame_cap}")
        tool("console_command", command=f"fps_max_inactive {args.frame_cap}")
        for index, (case, population, connections) in enumerate(plan, first_index):
            if population >= 10000 and case != "IdleSynced" and skip_large:
                print(f"SKIP {index}: larger population exceeds the exploratory safety budget", flush=True)
                continue
            summary = run(index, case, population, connections)
            if not summary["valid"] or summary["meanMs"] > 100 or summary["peakWorkingSetBytes"] > 12 * 1024**3:
                skip_large = True
        if args.profile_dir and not args.smoke:
            ready = args.profile_dir / "ready.json"
            if not ready.exists():
                raise RuntimeError("Elevated profiler helper is not ready; diagnostic capture remains pending")
            if args.profile_case:
                case, population, connections = args.profile_case
                run(first_index + len(plan), case, int(population), int(connections), True)
            else:
                cases = [r for r in records if r["scenario"] == "Changing" and r["valid"] and not r["profiled"]]
                selected = max(cases, key=lambda r: (r["population"], r["fakeConnections"]))
                run(first_index + len(plan), "Changing", selected["population"], selected["fakeConnections"], True)
            print("Waiting for separate Superluminal capture export", flush=True)
            deadline = time.monotonic() + 300
            while time.monotonic() < deadline and not (args.profile_dir / "finished.json").exists():
                time.sleep(1)
            completion = json.loads((args.profile_dir / "finished.json").read_text())
            if completion["status"] != "captured":
                raise RuntimeError(f"Superluminal failed: {completion}")
        (batch / "status.json").write_text(json.dumps({"status": "complete", "runs": len(records), "invalidRuns": sum(not r["valid"] for r in records)}, indent=2))
    except Exception as error:
        (batch / "status.json").write_text(json.dumps({"status": "failed", "error": str(error), "runs": len(records)}, indent=2))
        raise
    finally:
        if call("editor_status")["IsPlaying"]:
            tool("play_stop")
        if edited:
            tool("close_scene", scene=scene, discardChanges=True)
        tool("open_scene", path=original_scene)
        tool("console_command", command=f"fps_max {original_cap}")
        tool("console_command", command=f"fps_max_inactive {original_inactive_cap}")
        print(f"Restored saved scene settings. Results: {batch}", flush=True)


if __name__ == "__main__":
    main()
