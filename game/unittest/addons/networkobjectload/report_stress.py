"""Generate graphs and a local report from retained network stress samples."""
import argparse
import csv
from collections import defaultdict
import html
import json
from pathlib import Path
import statistics

import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("batch", type=Path)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    output = args.output or Path("D:/Sandbox.Public.Projects/reports/network-object-load") / args.batch.name
    output.mkdir(parents=True, exist_ok=True)
    all_rows = json.loads((args.batch / "results.json").read_text())
    rows = [r for r in all_rows if r["valid"] and not r["profiled"] and r["exceptions"] == 0]
    for r in rows:
        r["networkMsPerSecond"] = r["networkMeanMs"] * r["samples"] / r["actualSeconds"]
        r["allocationMiBPerSecond"] = r["totalAllocationBytes"] / 1024**2 / r["actualSeconds"]
        r["gcPauseMsPerSecond"] = r["gcPauseMs"] / r["actualSeconds"]
    keys = ["scenario", "population", "fakeConnections", "meanMs", "medianMs", "p95Ms", "p99Ms", "networkMeanMs", "networkP99Ms", "networkMsPerSecond", "allocationMiBPerSecond", "gcPauseMsPerSecond", "gen0", "gen1", "gen2", "peakWorkingSetBytes", "samples", "actualSeconds", "runTicks", "folder"]
    with (output / "summary.csv").open("w", newline="") as file:
        writer = csv.DictWriter(file, fieldnames=keys, extrasaction="ignore")
        writer.writeheader()
        writer.writerows(rows)
    groups = defaultdict(list)
    for r in rows:
        groups[(r["scenario"], r["population"], r["fakeConnections"])].append(r)
    cohorts = list(groups)
    labels = [f"{case}\n{population:,} / {connections} fake\nn={len(groups[(case, population, connections)])}" for case, population, connections in cohorts]
    plt.rcParams.update({"font.size": 9, "axes.grid": True, "grid.alpha": 0.2})

    def bars(ax, metric, ylabel, color):
        series = [[r[metric] for r in groups[c]] for c in cohorts]
        medians = [statistics.median(v) for v in series]
        errors = [[m - min(v) for m, v in zip(medians, series)], [max(v) - m for m, v in zip(medians, series)]]
        ax.bar(range(len(cohorts)), medians, yerr=errors, capsize=3, color=color)
        ax.set_xticks(range(len(cohorts)), labels, rotation=30, ha="right")
        ax.set_ylabel(ylabel)

    fig, axes = plt.subplots(2, 1, figsize=(14, 10), layout="constrained")
    bars(axes[0], "networkMsPerSecond", "Network scope elapsed ms / wall second", "#348abd")
    bars(axes[1], "networkP99Ms", "Network scope p99 ms / frame", "#e24a33")
    fig.suptitle("Synthetic server networking — median across repeats; whiskers show full repeat range")
    fig.savefig(output / "network-scaling.png", dpi=160)
    plt.close(fig)

    fig, axes = plt.subplots(3, 1, figsize=(14, 13), layout="constrained")
    bars(axes[0], "allocationMiBPerSecond", "Engine allocation counter MiB / second", "#988ed5")
    bars(axes[1], "gcPauseMsPerSecond", "GC pause ms / wall second", "#fbc15e")
    bars(axes[2], "p99Ms", "Full editor-host frame p99 ms", "#8eba42")
    fig.suptitle("Memory and tail frames — includes editor activity; fake transport")
    fig.savefig(output / "memory-gc.png", dpi=160)
    plt.close(fig)

    largest = max((r for r in rows if r["scenario"] == "Changing"), key=lambda r: r["networkMsPerSecond"], default=max(rows, key=lambda r: r["networkMsPerSecond"]))
    with (args.batch / largest["folder"] / "samples.csv").open() as file:
        samples = list(csv.DictReader(file))
    times = [float(s["seconds"]) for s in samples]
    fig, axes = plt.subplots(5, 1, figsize=(13, 12), sharex=True, layout="constrained")
    for ax, metric, ylabel, scale in zip(axes, ["frame_ms", "network_ms", "allocation_bytes", "working_set_bytes", "gc_pause_ticks"],
        ["Full frame ms", "Network ms / frame", "Allocation KiB / frame", "Process working set GiB", "GC pause ms / frame"], [1, 1, 1/1024, 1/1024**3, 1/10000]):
        ax.plot(times, [float(s[metric]) * scale for s in samples], linewidth=0.6)
        ax.set_ylabel(ylabel)
    axes[-1].set_xlabel("Seconds after measurement start (counters describe preceding frame)")
    fig.suptitle(f"Changing stress example: {largest['scenario']}, {largest['population']:,} objects, {largest['fakeConnections']} fake connections")
    fig.savefig(output / "timeline-largest.png", dpi=160)
    plt.close(fig)

    scopes = ["networkMeanMs", "updateMeanMs", "renderMeanMs", "editorMeanMs", "idleMeanMs"]
    fig, ax = plt.subplots(figsize=(10, 5), layout="constrained")
    ax.bar([s.replace("MeanMs", "") for s in scopes], [largest[s] for s in scopes])
    ax.set_ylabel("Mean elapsed ms / frame (scopes can overlap)")
    ax.set_title("Engine scopes in the unprofiled stress example")
    fig.savefig(output / "engine-scopes.png", dpi=160)
    plt.close(fig)

    figures = ["network-scaling", "memory-gc", "timeline-largest", "engine-scopes"]
    diagnostic = args.batch / "diagnostics"
    coverage = None
    if (diagnostic / "cpu-summary.json").exists():
        coverage = json.loads((diagnostic / "cpu-summary.json").read_text())
        with (diagnostic / "cpu-hotspots.csv").open() as file:
            hotspots = list(csv.DictReader(file))
        total = coverage["totalSamples"]
        fig, axes = plt.subplots(3, 1, figsize=(15, 17), layout="constrained")
        for index, (ax, metric, label) in enumerate(zip(axes, ["self_samples", "inclusive_samples", "inclusive_samples"], ["Leaf CPU sample share", "Inclusive CPU sample share (overlaps)", "Networking inclusive CPU sample share (overlaps)"])):
            candidates = [r for r in hotspots if "Sandbox.Network" in r["function"] or "DeltaSnapshot" in r["function"]] if index == 2 else hotspots
            top = sorted(candidates, key=lambda r: int(r[metric]), reverse=True)[:12][::-1]
            names = [r["function"] if len(r["function"]) < 120 else r["function"][:117] + "..." for r in top]
            ax.barh(names, [int(r[metric]) * 100 / total for r in top])
            ax.set_xlabel(label + " (%)")
        profiled = next((r for r in reversed(all_rows) if r["profiled"]), None)
        title = f"{profiled['scenario']}, {profiled['population']:,} objects / {profiled['fakeConnections']} fake peers" if profiled else "workload recorded in diagnostic manifest"
        fig.suptitle("Separate Superluminal diagnostic — " + title)
        fig.savefig(output / "cpu-hotspots.png", dpi=160)
        plt.close(fig)
        with (diagnostic / "cpu-threads.csv").open() as file:
            threads = list(csv.DictReader(file))[:12]
        fig, ax = plt.subplots(figsize=(11, 5), layout="constrained")
        ax.bar([r["thread_id"] for r in threads], [int(r["samples"]) for r in threads])
        ax.set_xlabel("Thread ID")
        ax.set_ylabel("CPU sampling hits")
        ax.set_title("Diagnostic CPU samples by thread (not elapsed milliseconds)")
        fig.savefig(output / "cpu-threads.png", dpi=160)
        plt.close(fig)
        figures += ["cpu-hotspots", "cpu-threads"]

    lines = ["# Network object stress test", "", "Editor-host synthetic workload. No client processes or real transport. Runs execute 1,500 fixed workload ticks after 500 warmup ticks. Figures use unprofiled, completed runs with zero engine exceptions.", "",
        "Network scopes measure elapsed engine work, not whole server tick CPU. Scope totals can overlap. Counter samples lag by one frame. Frame rates differ between cases, so network elapsed time and allocations per wall second are the primary scaling metrics. Active and inactive frame caps are 90. This is a provisional local baseline; renderer, resolution and editor activity remain confounders.", "",
        "| Workload | Objects | Fake connections | Repeats | Network ms/s (median; range) | Network p99 ms/frame | Alloc MiB/s | Frame p99 ms |", "|---|---:|---:|---:|---:|---:|---:|---:|"]
    for cohort in cohorts:
        values = groups[cohort]
        median = lambda key: statistics.median(r[key] for r in values)
        network = [r["networkMsPerSecond"] for r in values]
        lines.append(f"| {cohort[0]} | {cohort[1]:,} | {cohort[2]} | {len(values)} | {median('networkMsPerSecond'):.1f} ({min(network):.1f}–{max(network):.1f}) | {median('networkP99Ms'):.2f} | {median('allocationMiBPerSecond'):.1f} | {median('p99Ms'):.2f} |")
    lines += ["", "| Workload | Objects / fake | Frame mean / median / p95 / p99 ms | GC counts 0 / 1 / 2 | Peak process GiB |", "|---|---|---|---|---|"]
    for cohort in cohorts:
        values = groups[cohort]
        med = lambda key: statistics.median(r[key] for r in values)
        lines.append(f"| {cohort[0]} | {cohort[1]:,} / {cohort[2]} | " + " / ".join(f"{med(k):.2f}" for k in ["meanMs", "medianMs", "p95Ms", "p99Ms"]) + f" | {med('gen0'):g} / {med('gen1'):g} / {med('gen2'):g} | {med('peakWorkingSetBytes') / 1024**3:.2f} |")
    lines += ["", "Excluded runs:"]
    for r in all_rows:
        if not r["valid"] or r["profiled"]:
            lines.append(f"- `{r['folder']}`: " + ("separate profiled diagnostic" if r["profiled"] else f"{r['reason']}; completed {r['runTicks']}/{r['requestedTicks']} ticks"))
    if coverage:
        lines += ["", f"Separate CPU capture: {coverage['totalSamples']:,} samples; {coverage['missingStacks']:,} missing stacks; {coverage['unresolvedLeafSamples']:,} unresolved leaves. Inclusive CPU shares overlap. {coverage['allocationInterpretation']}", "", "![CPU hotspots](cpu-hotspots.png)", "", "![CPU threads](cpu-threads.png)"]
    lines += ["", "![Network scaling](network-scaling.png)", "", "![Memory and tails](memory-gc.png)", "", "![Highest-cost timeline](timeline-largest.png)", "", f"Raw batch: `{args.batch}`. Each repeat is retained separately with its samples, manifest, console output and binary/source hashes. Failed/profiled runs are excluded from these graphs."]
    (output / "report.md").write_text("\n".join(lines) + "\n", encoding="utf-8")
    table = "<table><tr>" + "".join(f"<th>{html.escape(k)}</th>" for k in keys[:9]) + "</tr>"
    for r in rows:
        table += "<tr>" + "".join(f"<td>{html.escape(str(round(r[k], 3) if isinstance(r[k], float) else r[k]))}</td>" for k in keys[:9]) + "</tr>"
    table += "</table>"
    (output / "index.html").write_text('<!doctype html><meta charset="utf-8"><title>Network stress report</title><style>body{font:16px system-ui;max-width:1400px;margin:30px auto;padding:20px}img{width:100%}table{border-collapse:collapse}td,th{padding:8px;border:1px solid #ddd}</style><h1>Network object stress test</h1><p>Editor host, fake connections, unprofiled measurements. Network scope elapsed time is not whole server tick CPU. Full repeat ranges shown. Real transport and client correctness are excluded.</p><p><a href="report.md">Report</a> · <a href="findings.md">Investigation</a> · <a href="summary.csv">Summary CSV</a></p>' + "".join(f'<img src="{name}.png">' for name in figures) + table, encoding="utf-8")
    print(output)


if __name__ == "__main__":
    main()
