"""Compare matched network batches; retain per-run measurements and reject incompatible cohorts."""
import argparse
import json
from pathlib import Path
import statistics

import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt


def load(folder):
    environment = json.loads((folder / "environment.json").read_text())
    groups = {}
    for row in json.loads((folder / "results.json").read_text()):
        if row["profiled"] or not row["valid"] or row["exceptions"]:
            raise ValueError(f"Invalid/profiled run cannot enter this comparison: {row['folder']}")
        manifest = json.loads((folder / row["folder"] / "manifest.json").read_text())
        if row["runTicks"] != manifest["RunTicks"]:
            raise ValueError("Incomplete workload")
        expected = row["population"] * row["runTicks"] if row["scenario"] == "Changing" else 0
        if row["scenario"] in ("Changing", "IdleSynced") and row["objectUpdates"] != expected:
            raise ValueError("Unexpected mutation count")
        if row.get("realConnections", 0) > 0 and not manifest["replicationChecked"]:
            raise ValueError("Actual-client convergence was not checked")
        key = (row["scenario"], row["population"], row["fakeConnections"], row.get("realConnections", 0))
        row = dict(row, networkMsPerSecond=row["networkMeanMs"] * row["samples"] / row["actualSeconds"],
                   allocationMiBPerSecond=row["totalAllocationBytes"] / 1024**2 / row["actualSeconds"])
        groups.setdefault(key, []).append((row, manifest))
    return environment, groups


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("baseline", type=Path)
    parser.add_argument("candidate", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    before_env, before = load(args.baseline)
    after_env, after = load(args.candidate)
    for field in ("sourceSha256", "nativeSha256", "hardware", "benchmarkFrameCap", "inactiveFrameCap", "localClient", "runnerSha256", "projectSha256"):
        if before_env[field] != after_env[field]:
            raise ValueError(f"Incompatible environment: {field}")
    if not before or before.keys() != after.keys():
        raise ValueError("Workload sets must match")
    manifest_fields = ("PropertyGroups", "ActiveFraction", "ChildrenPerObject", "WarmupTicks", "RunTicks", "RenderSubjects", "MinimumClients", "SettleSeconds", "actualFixedFrequency", "actualNetworkFrequency")
    for key in before:
        expected = {field: before[key][0][1][field] for field in manifest_fields}
        for _, manifest in before[key] + after[key]:
            if {field: manifest[field] for field in manifest_fields} != expected:
                raise ValueError(f"Incompatible settings for {key}")
    supporting_differences = [name for name, digest in before_env["managedSha256"].items()
                             if name != "Sandbox.Engine.dll" and after_env["managedSha256"].get(name) != digest]
    keys = sorted(before)
    metrics = [("networkMeanMs", "Network elapsed ms/frame"), ("networkMsPerSecond", "Network elapsed ms/s"),
               ("allocationMiBPerSecond", "Managed allocation MiB/s"), ("p99Ms", "Frame p99 ms")]
    args.output.mkdir(parents=True, exist_ok=True)
    fig, axes = plt.subplots(2, 2, figsize=(13, 9))
    labels = [f"{case}\n{population:,}; {fake} fake / {real} real" for case, population, fake, real in keys]
    for axis, (metric, title) in zip(axes.flat, metrics):
        for offset, groups, label, color in ((-.18, before, "Before", "#536b89"), (.18, after, "After", "#36a580")):
            series = [[row[metric] for row, _ in groups[key]] for key in keys]
            medians = [statistics.median(values) for values in series]
            errors = [[median - min(values) for median, values in zip(medians, series)],
                      [max(values) - median for median, values in zip(medians, series)]]
            axis.bar([i + offset for i in range(len(keys))], medians, width=.36, yerr=errors, capsize=3, label=label, color=color)
        axis.set_xticks(range(len(keys)), labels, rotation=20, ha="right")
        axis.set_ylabel(title)
        axis.grid(axis="y", alpha=.2)
        axis.legend()
    fig.suptitle("Delta snapshot scheduling — matched editor-host workloads")
    fig.tight_layout()
    fig.savefig(args.output / "comparison.png", dpi=160)
    plt.close(fig)
    lines = ["# Delta snapshot scheduling comparison", "", f"Before: `{before_env['commit']}`. After: `{after_env['commit']}`.", "",
             "Harness source, runner, native binaries, hardware, frame caps, client counts and workload settings match. Graphs show medians and full repeat ranges; single runs are exploratory, not a repeatability claim. Local clients share CPU/GPU with the editor. Network timings are overlapping elapsed scopes, not an additive CPU breakdown. Resolution/quality/vsync remain unrecorded. New Superluminal attribution and dedicated-server validation are pending.", "",
             f"Supporting managed assemblies with different hashes: {', '.join(supporting_differences) or 'none'}.", "",
             "| Case | Before / after repeats | Mean frame ms | Frame p99 ms | Network ms/frame | Network ms/s | Allocation MiB/s |", "|---|---:|---:|---:|---:|---:|---:|"]
    for key in keys:
        values = []
        for metric in ("meanMs", "p99Ms", "networkMeanMs", "networkMsPerSecond", "allocationMiBPerSecond"):
            values.append(" -> ".join(f"{statistics.median(row[metric] for row, _ in groups[key]):.3f}" for groups in (before, after)))
        lines.append(f"| {key[0]}/{key[1]} ({key[2]} fake/{key[3]} real) | {len(before[key])}/{len(after[key])} | " + " | ".join(values) + " |")
    lines += ["", "![Comparison](comparison.png)", "", f"Raw baseline: `{args.baseline}`. Raw candidate: `{args.candidate}`.", "",
              "Per-run frame p95, allocation/GC rates, working set, timelines and workload counts remain in the individual batch reports and raw exports."]
    (args.output / "comparison.md").write_text("\n".join(lines) + "\n", encoding="utf-8")
    (args.output / "comparison.json").write_text(json.dumps({"beforeEnvironment": before_env, "afterEnvironment": after_env,
                                                            "before": {str(key): values for key, values in before.items()},
                                                            "after": {str(key): values for key, values in after.items()}}, indent=2))
    (args.output / "index.html").write_text('<!doctype html><meta charset="utf-8"><title>Snapshot scheduling</title><style>body{font:16px system-ui;max-width:1400px;margin:2rem}img{max-width:100%}</style><h1>Delta snapshot scheduling</h1><p><a href="comparison.md">Matched measurements and limitations</a> · <a href="findings.md">Findings</a></p><img src="comparison.png">', encoding="utf-8")
    print(args.output)


if __name__ == "__main__":
    main()
