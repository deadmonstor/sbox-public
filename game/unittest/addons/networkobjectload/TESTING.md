# Delta snapshot testing

Start here for the current engine changes, runnable projects, correctness coverage and retained measurements.

## Repository and projects

The versioned harness is in `D:/sbox-public`, repository `https://github.com/Facepunch/sbox-public`, at `game/unittest/addons/networkobjectload/`. It was restored from `josh-branch` onto `perf/replication-dirty-state`; no combined-branch engine changes were imported. Keep the same harness available on both builds used for comparison. Never use `josh-branch` as a PR source.

- [Network Object Load](README.md): 19 deterministic scenes, network/frame scopes, allocations, GC and local exports. Open [.sbproj](.sbproj).
- [Network Dormancy Lab](D:/Sandbox.Public.Projects/projects/network-dormancy-lab/README.md): ten multiplayer correctness scenes for visibility, nested transforms, reparenting, enabled state, scalar/query/reliable collections, late join, ownership and getter destruction. Its old branch name identifies the original target; current scheduler execution must be recorded separately.
- [Project library and catalog](D:/Sandbox.Public.Projects/README.md): inventory and historical validation.
- [Network report index](D:/Sandbox.Public.Projects/reports/network-object-load/README.md): investigations, graphs and current evidence.

## Engine changes

Branch: `perf/replication-dirty-state`, based on master `da927017a`.

| Commit | Change |
|---|---|
| `b437aa1af` | Reuse unchanged scalar preparation |
| `f6bfdf23c` | Delta table mutation notifications |
| `95eda4fae` | Cache tracked delta preparation per property, including polling siblings |
| `4554e2fc8` | Schedule dirty, continuously evaluated and unacknowledged objects; acknowledged idle objects leave the send pass |

Unknown delta implementations, query properties, dynamic visibility and transform proxies remain continuously evaluated. Unacknowledged snapshots stay active and advance IDs. Mutations, recipient changes and hotload must wake objects.

For a scheduler-only comparison, use baseline `95eda4fae` and candidate `4554e2fc8`. For all preparation changes, use master `da927017a` and candidate `4554e2fc8`. Use identical harness source/settings, local branches in this checkout, close the editor before rebuilding, restart for each build and record DLL/native/source hashes. October 8 results use different engine/native bases; they are historical references, not matched controls.

## Correctness

Developer build, format verification and 148 selected network tests passed for `4554e2fc8`: 46 unit, 54 engine and 48 integration cases, including 17 scheduling cases. This establishes automated regression coverage, not a performance improvement or completion of the multiplayer matrix.

```powershell
dotnet run --project engine/Tools/SboxBuild/SboxBuild.csproj -- build --config Developer --skip-native
dotnet run --project engine/Tools/SboxBuild/SboxBuild.csproj --no-build -- test --no-build --filter 'FullyQualifiedName~SceneTests.GameObjects.NetworkTest|FullyQualifiedName~NetworkTests'
dotnet run --project engine/Tools/SboxBuild/SboxBuild.csproj --no-build -- format --verify
python game/unittest/addons/networkobjectload/verify.py
```

Engine/integration runs require `FACEPUNCH_ENGINE` pointing to `D:/sbox-public/game`; SboxBuild configures its test runs. Project API compilation and scene validation passed: 19 scenes, 231 unique GUIDs, zero warnings/errors. Current editor compilation passed too.

| Contract | Automated test | Multiplayer follow-up |
|---|---|---|
| Scalar/delta/transform/metadata wakeups | `IdleSnapshotWakesOnMutation` (10 cases) | Dormancy scenes 1, 3, 4, 5 |
| Mutations during preparation | `ChangesDuringPreparationRemainScheduled` | Getter destruction scene 10 |
| Query/reliable collection polling | `ReliableQueryPropertiesKeepPolling`, `SnapshotPreparationTest` | Scenes 6 and 7 |
| Visibility continues evaluating | `PollingAndVisibilityObjectsRemainScheduled` | Scene 2, load Visibility |
| Missing ACKs keep delivery active | `UnacknowledgedSnapshotsRemainScheduled` | Fake-peer stress and impaired real clients |
| Recipients, hotload, ownership | `IdleSnapshotsWakeForConnectionChangesAndHotload`, mutation cases | Scenes 8 and 9, load LateJoin |
| Queued object destruction | `DestroyedObjectsLeaveSnapshotQueues` | Scene 10, load Lifecycle |

Source: [scheduling tests](../../../../engine/Tests/Sandbox.Test.Integration/Scene/GameObjects/Network.Scheduling.cs), [preparation tests](../../../../engine/Tests/Sandbox.Test.Engine/Network/SnapshotPreparation.cs). [Current evidence folder](D:/Sandbox.Public.Projects/results/network-object-load/replication-dirty-state-20261010) retains build/test/format/project logs and explicit validation status.

## Benchmark cohorts

Fake peers are `EmptyConnection`: they receive no packets and return no ACKs. Their idle objects remain pending. Keep those cases for pending-delivery cost; they cannot demonstrate acknowledged objects leaving the queue.

With this project compiled, Play stopped and scene changes saved:

```powershell
# Existing four cases: 32 fake peers, no ACKs.
python game/unittest/addons/networkobjectload/run_stress.py --plan game/unittest/addons/networkobjectload/four-case-plan.json

# Actual client: transport and final-state convergence, zero fake peers.
python game/unittest/addons/networkobjectload/run_stress.py --local-client --plan game/unittest/addons/networkobjectload/acknowledged-case-plan.json

# Short function check, not a performance comparison.
python game/unittest/addons/networkobjectload/run_stress.py --local-client --smoke

# Generate graphs outside measurement.
D:/Sandbox.Public.Projects/.benchmark-env/Scripts/python.exe game/unittest/addons/networkobjectload/report_stress.py <batch-directory>
```

Both four-case plans use idle/10,000, changing/1,000, idle/100 and changing/100. The acknowledged plan uses one actual local client and zero fake peers. The runner starts/cleans up its own client per case, preserves failures, restores scenes/frame caps and records project/runner/binary hashes and real-client counts. A local client shares CPU/GPU with the editor: this is an editor-host cohort, not dedicated-server throughput.

Default warmup: 500 fixed ticks. Measurement: 1,500 ticks. Simulation: 50Hz; network: 30Hz; active/inactive frame cap: 90. Match settings and work counts across builds. One pair is provisional. `--heavy-repeats 5` repeats the default fake-peer plan; repeat custom plan entries explicitly. Allow initial replication to settle before accepting idle timing. Final-state digests establish convergence, not precise ACK latency.

Raw immutable runs live under `D:/Sandbox.Public.Projects/results/network-object-load/<unique-run>/`; graphs/findings under `reports/network-object-load/<cohort>/`. Full before/after comparisons and current CPU captures are pending unless listed as completed in the validation record. Superluminal requires Administrator approval; keep those captures separate from timing runs and follow the project README.

## Scheduling comparison — 2026-10-10

[Graphs and comparison](D:/Sandbox.Public.Projects/reports/network-object-load/replication-scheduling-20261010/index.html), [findings](D:/Sandbox.Public.Projects/reports/network-object-load/replication-scheduling-20261010/findings.md). All four cases passed on both builds: one actual local client, zero fake peers, 500 warmup ticks and 1,500 measured ticks per case. Final state matched in every run; measurement exceptions were zero. The changing/1,000 cases each completed 1.5 million object updates. The short actual-client smoke passed separately; the first failed runner startup attempt is retained.

| Workload | Network elapsed ms/frame, before -> after | Allocation MiB/s, before -> after |
|---|---:|---:|
| Idle, 10,000 | 1.723 -> 0.012 | 1.437 -> 1.567 |
| Changing, 1,000 | 1.701 -> 1.801 | 16.257 -> 16.191 |
| Idle, 100 | 0.027 -> 0.012 | 1.456 -> 1.560 |
| Changing, 100 | 0.173 -> 0.186 | 2.944 -> 3.053 |

This first pair shows substantially less acknowledged idle network work, with slightly higher changing costs. It establishes neither repeatability nor an allocation improvement. Frames remained near the 90fps cap. Native/harness/settings match, but supporting managed DLL hashes differ after rebuilding; their source trees are unchanged. Client and editor share hardware, and resolution/quality/vsync remain unrecorded. Raw per-run frame percentiles, GC, working set, timelines and exact counts are in the linked reports.

Reproduce the graphs using existing raw exports:

```powershell
D:/Sandbox.Public.Projects/.benchmark-env/Scripts/python.exe game/unittest/addons/networkobjectload/compare_stress.py D:/Sandbox.Public.Projects/results/network-object-load/replication-scheduling-baseline-client-20261010 D:/Sandbox.Public.Projects/results/network-object-load/replication-scheduling-client-20261010 D:/Sandbox.Public.Projects/reports/network-object-load/replication-scheduling-20261010
```

The baseline is retained on `perf/replication-scheduling-baseline-20261010` at `dce32b54b`: engine `95eda4fae` plus identical harness source. The feature branch contains the scheduler and restored test project. Current fake-peer performance reruns, the ten-scene multiplayer matrix, Superluminal and dedicated-server runs remain pending.

## Next investigations

### Notification coalescing trial — 2026-10-10

Repeated notifications now skip continuous-set updates when the object is already pending. Revisions still advance on every notification; continuous membership is refreshed before processing to preserve queued visibility/polling changes. Developer build, format verification and 150 selected network tests passed, including two new queued-visibility cases.

[Trial graphs](D:/Sandbox.Public.Projects/reports/network-object-load/replication-coalescing-comparison-20261010/index.html) compare retained scheduler results with six new actual-client runs, without rerunning the baseline. All six passed convergence and work-count checks with zero measurement exceptions. Changing/1,000 network elapsed cost was 1.753, 1.906 and 1.809 ms/frame (median 1.809), versus the previous single 1.801 run: no demonstrated throughput improvement. Frame p99 median was 14.795 ms versus 16.788; allocations were 16.140 versus 16.191 MiB/s. Neither establishes a repeatable benefit against a single older control. Idle/10,000 remains approximately 0.012 ms/frame; changing/100 is 0.185 versus 0.186.

The measured candidate was `eca36bc09` plus the retained `tested.patch` in `D:/Sandbox.Public.Projects/results/network-object-load/replication-coalescing-client-20261010`; binary hashes and dirty diff hash are recorded. Harness/native/settings match; supporting managed hashes differ after rebuilding. No new profiling or dedicated-server run was performed. Next investigate candidate-set construction and per-send ACK/retention checks rather than assuming notifications explain the changing cost.

Repeat matched baseline/candidate cohorts and compare mean/median/p95/p99 network/frame scopes, allocation rates, GC and exact work counts. Retain repeats and failures. Run all ten multiplayer scenes plus impaired Mixed/Visibility/Lifecycle/LateJoin, then separate Superluminal attribution and dedicated-server measurements. Schema/quantization and network-budget changes remain future work.
