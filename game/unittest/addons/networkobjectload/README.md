# Network Object Load

Standalone s&box project for testing server replication costs at increasing object and client counts. Includes saved workloads, local frame/scope/allocation/GC exports, automated stress runs and reports. Synthetic editor-host benchmarks and four real-client settled-state correctness scenes have been run.

## Run

1. Open `networkobjectload/.sbproj` in s&box.
2. Open one scene from `Assets/scenes/`. The startup scene is `05-idle-synced`.
3. Select Scenario Controller and set Population (100 for a first smoke run, then 1,000 / 5,000 / 10,000). Set MinimumClients to the required remote client count.
4. Start a host and connect clients to its lobby. The HUD shows each phase. For Late Join, connect one additional client during Running.
5. Wait for PASS or FAIL and retain the host/client console output. Restart Play between configurations; scenes do not advance automatically.

The scene contains a lobby helper, host workload controller, status panel, camera and light. Subjects are created by the host in batches of 100 per fixed tick. RenderSubjects is off by default; turn it on only for a separately labelled visual run. Subjects have no update callbacks, physics or models unless rendering is enabled.

## Scenes

| Scene | Workload |
|---|---|
| 00-empty | Lobby/controller/HUD control |
| 01-local-idle | Static objects with four unchanged properties, never networked |
| 02-local-moving | Local transform work; compare with 07-moving |
| 03-local-changing | Local property work; compare with 08-changing |
| 04-idle-bare | Network roots without custom sync components |
| 05-idle-synced | Four unchanged sync properties per root |
| 06-idle-32-properties | Eight payload components, 32 unchanged sync properties |
| 07-moving | Transform change on every root each fixed tick |
| 08-changing | Four property changes on every root each fixed tick |
| 09-sparse-1-percent | One percent of roots change properties |
| 10-sparse-10-percent | Ten percent of roots change properties |
| 11-visibility | Explicit visibility policy hides roots, then reveals them halfway through |
| 12-lifecycle | Ten true destroy/spawn replacements every 50 fixed ticks |
| 13-late-join | An additional client receives the populated scene |
| 14-mixed | Ten percent moving/changing, lifecycle bursts and one reliable scene RPC per tick |
| 15-hierarchy | Moving roots with four children per root |
| 16-rpcs | Idle roots plus ten reliable scene RPCs per fixed tick |
| 17-fake-8 | Idle roots with eight fake connections; no real clients required |
| 18-fake-32 | Idle roots with 32 fake connections; no real clients required |

Set FakeConnections on any scene's Scenario Controller to add 0–256 engine empty connections before spawning subjects. Set MinimumClients=0 to run without real client processes, or keep it at 1 to combine fake load with a real client's correctness check. Fake connections do not satisfy MinimumClients, send state reports or count as late joiners. Restart Play between runs to reset the networking system and clear them.

These connections exercise server replication processing and encoding; the engine simulates delta-cluster acknowledgements. They have no transport send/receive, client simulation, latency or packet loss. Their timing is a separate synthetic benchmark, not a replacement for real multiplayer measurements. Fake-only runs finish with COMPLETE (replication unverified), never PASS. Use the visibility scene's explicit gate for fake visibility workloads because fake peers have no player/PVS origin.

PropertyGroups controls zero to eight payload components (four properties each). ActiveFraction selects a deterministic prefix of the population; no random input is involved. ChildrenPerObject adds descendants under each network root, rather than independent network objects. RpcsPerTick is scene-level reliable RPC traffic with an empty receiver; it does not simulate per-object gameplay RPCs. The local controls explicitly use NetworkMode.Never, including for late-join snapshots.

## Protocol and checks

At 50 fixed ticks/second, the default 500 warmup ticks and 1,500 workload ticks represent 10 and 30 seconds of simulation. The server executes the same number of workload ticks when wall time slows down. TimeoutSeconds stops excessively slow runs; increase it deliberately for a longer run. Spawn/warmup operations are excluded from final mutation/lifecycle/RPC counters. Warmup executes the same workload as Running except for the visibility reveal.

After Running, state freezes for SettleSeconds. Every connected client reports its final subject count and a digest of root/child IDs, names, positions and all payload fields. Positions are rounded to 0.1 units. Missing, duplicate, stale or incorrectly replaced subjects fail the check. Initial participants must still be connected; Late Join also requires an additional participant. Final convergence does not prove intermediate visibility, update freshness or RPC delivery. Very large populations may need longer settling before checking.

The visibility scene uses Component.INetworkVisible and AlwaysTransmit=false; it measures an explicit visibility gate, not camera-distance interest management. All subjects are host-controlled. Client-owned workloads, query/reliable collections, enabled-state changes, physics and deeper hierarchies can be added as distinct variants later. Increasing PropertyGroups also increases component count; it does not isolate property count alone.

MinimumClients=0 permits a host-only smoke run; it cannot pass multiplayer replication validation. MaxPlayers is 16 total (host plus up to 15 remote clients). For CPU comparisons, run clients on a different machine and hold build, network settings and workload counts constant. Logs and the HUD update at phase boundaries; client digests run after the workload, outside future timing intervals.

Expected: all required real clients match settled state and the HUD shows PASS. Runs without real clients show COMPLETE (replication unverified). A failed check or timeout shows FAIL. CaptureMetrics exports samples.csv, summary.json and manifest.json after completion under `game/data/local/networkobjectload#local/network-object-load/`. Network is an elapsed engine scope; frame and allocation counters include editor activity. Bandwidth and real transport latency are not measured.

## Automated stress test

With this project open, compilation complete, Play stopped and scenes saved, run from `D:/sbox-public`:

```powershell
python game/unittest/addons/networkobjectload/run_stress.py
```

The runner uses editor MCP at `127.0.0.1:7269`, holds active/inactive frame caps at 90 on this machine, reopens the saved fake-32 scene for each temporary configuration, and restores the original scene and active frame cap afterward. The default is one run each of IdleSynced/10,000, Changing/1,000, IdleSynced/100 and Changing/100, all with 32 fake connections. Run these four cases before and after each engine change. No empty case or automatic repeats are included. The same matrix is saved in `four-case-plan.json`. Excessively slow changing cases trigger the timeout; invalid runs remain in the results. `--plan <json>` accepts an array of `[scenario, population, fakeConnections]` cases. `--resume --batch <existing-batch>` appends only when measured source/binary hashes and caps match.

Generate graphs after measurement, using the library's plotting environment:

```powershell
D:/Sandbox.Public.Projects/.benchmark-env/Scripts/python.exe game/unittest/addons/networkobjectload/report_stress.py <batch-directory>
```

Superluminal runs separately and requires an approved Administrator PowerShell helper:

```powershell
./game/unittest/addons/networkobjectload/profile_stress.ps1 -TargetProcessId <sbox-dev-pid> -OutputDirectory <diagnostic-directory>
```

Start that helper with a normal UAC prompt after approval, then add `--profile-dir <diagnostic-directory>` to the stress command. Use a fresh diagnostic directory for each capture. The runner waits for ready.json, triggers a separate changing workload, and excludes its measurements from the unprofiled report. Export CPU/scopes with the library's `TraceSummary.dll` and put the diagnostic exports in `<batch>/diagnostics/` before regenerating the report.

## Measured baseline — 2026-10-08

[Graphs and full report](D:/Sandbox.Public.Projects/reports/network-object-load/batch-20261008-170500/index.html), [investigation](D:/Sandbox.Public.Projects/reports/network-object-load/batch-20261008-170500/findings.md), raw batch `D:/Sandbox.Public.Projects/results/network-object-load/batch-20261008-170500`.

16 valid unprofiled runs, one timed-out stress case and one separate Superluminal diagnostic. Ryzen 9 5900X, editor host, no rendered subjects, four synced properties per root, 50Hz fixed simulation, 30Hz network updates. Valid runs completed 1,500 measured ticks after 500 warmup ticks. Five repeats each at 1,000 objects/32 fake connections:

| Workload | Mean frame ms, median across repeats | Frame p99 ms, median across repeats | Network elapsed ms/s | Allocation MiB/s |
|---|---:|---:|---:|---:|
| Idle, 1,000 / 32 fake | 11.11 | 11.15 | 55.1 | 2.5 |
| Changing, 1,000 / 32 fake | 92.68 | 166.06 | 872.9 | 26.7 |
| Idle, 10,000 / 32 fake (one run) | 42.15 | 46.51 | 934.0 | 0.9 |
| Mixed, 1,000 / 32 fake (one run) | 11.22 | 20.78 | 231.4 | 10.4 |

Changing/5,000/32 fake timed out after 180 seconds at 986/1,500 workload ticks, with approximately 910ms mean frames; it is excluded from completed-run comparisons. Changing/10,000 was not attempted after this limit. The separately profiled Changing/1,000/8 run resolves sending and acknowledgement processing as CPU leads. Allocation ownership is unavailable from that CPU trace, and 53.5% of leaf samples are unresolved. These are local synthetic measurements, not dedicated-server or real-client throughput guarantees. Remaining scene variants have been designed/compiled but were not all benchmarked.

## Validation

Run `python game/unittest/addons/networkobjectload/verify.py` from the engine checkout. It validates scene/settings JSON, GUID uniqueness and required components, then compiles only addon code against existing local managed assemblies with the engine code generators. Generated verification output is ignored. Actual check status is recorded in `workloads.json`; local compilation does not establish editor sandbox or multiplayer success.

## Idle preparation prototype — 2026-10-08

Branch `perf/network-idle-scan`; [before/after report](D:/Sandbox.Public.Projects/reports/network-object-load/idle-scan-20261008/index.html). Five repeats per build of IdleSynced/10,000/32 and Changing/1,000/32 completed identical workload counts. Median idle network scope: 22.091ms to 9.134ms, with overlapping ranges. Whole-editor idle allocation rate increased at the original 90fps cap; a separate five-repeat-per-build 20fps control measured essentially unchanged allocation rates (0.787 versus 0.789MiB/s). This is a prototype, with broader wake-up and dedicated-server checks pending.

Mixed, Visibility, Lifecycle and LateJoin passed settled-state checks with actual clients and no fake peers; LateJoin used two clients. These checks do not validate adversarial acknowledgements. Separate Idle/10,000/32 and Changing/1,000/32 CPU captures plus a changing allocation trace are retained. No ACK/send optimization is included. `--frame-cap` supports controlled comparisons, defaulting to 90; `--profile-case SCENARIO POPULATION CONNECTIONS` selects a separate diagnostic workload.

## Acknowledgement bookkeeping — 2026-10-08

Branch `perf/network-snapshot-acks`; [four-case before/after report](D:/Sandbox.Public.Projects/reports/network-object-load/ack-bookkeeping-20261008/index.html). Seven new unit tests and five production-serializer/decoder integration cases pass, covering delayed, duplicated, reordered and missing ACKs, adjacent snapshot-ID wrapping and values returning to an older state while updates remain in flight. The original engine fails two reordered-ACK regressions. The change retains the full slot walk and avoids unnecessary acknowledgement-set mutations.

One run per build of each approved case completed 1,500 workload ticks. Idle/10,000 network scope fell from 25.447ms to 3.224ms; changing/1,000 fell from 72.499ms to 69.534ms, with worse p99 frames and GC pauses. Whole-editor idle allocation rate rose as frame throughput increased. These single pairs do not establish repeatability or a changing-workload performance win. An earlier after cohort was superseded after finding an in-flight-value correctness edge case; both cohorts are retained.

Mixed, Visibility, Lifecycle and LateJoin passed again with real clients, 100ms configured host lag and 10% configured packet loss, with sampling disabled. LateJoin used two clients. Broader ownership/query/transform wake-up coverage, dedicated-server comparisons and sending optimizations remain pending.
