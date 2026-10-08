# Network Object Load

Standalone s&box project for testing server replication costs at increasing object and client counts. No specific issue or performance defect is claimed. This implements the workload matrix; timing collection and profiling are separate next steps.

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

PropertyGroups controls zero to eight payload components (four properties each). ActiveFraction selects a deterministic prefix of the population; no random input is involved. ChildrenPerObject adds descendants under each network root, rather than independent network objects. RpcsPerTick is scene-level reliable RPC traffic with an empty receiver; it does not simulate per-object gameplay RPCs. The local controls explicitly use NetworkMode.Never, including for late-join snapshots.

## Protocol and checks

At 50 fixed ticks/second, the default 500 warmup ticks and 1,500 workload ticks represent 10 and 30 seconds of simulation. The server executes the same number of workload ticks when wall time slows down. TimeoutSeconds stops excessively slow runs; increase it deliberately for a longer run. Spawn/warmup operations are excluded from final mutation/lifecycle/RPC counters. Warmup executes the same workload as Running except for the visibility reveal.

After Running, state freezes for SettleSeconds. Every connected client reports its final subject count and a digest of root/child IDs, names, positions and all payload fields. Positions are rounded to 0.1 units. Missing, duplicate, stale or incorrectly replaced subjects fail the check. Initial participants must still be connected; Late Join also requires an additional participant. Final convergence does not prove intermediate visibility, update freshness or RPC delivery. Very large populations may need longer settling before checking.

The visibility scene uses Component.INetworkVisible and AlwaysTransmit=false; it measures an explicit visibility gate, not camera-distance interest management. All subjects are host-controlled. Client-owned workloads, query/reliable collections, enabled-state changes, physics and deeper hierarchies can be added as distinct variants later. Increasing PropertyGroups also increases component count; it does not isolate property count alone.

MinimumClients=0 permits a host-only smoke run; it cannot pass multiplayer replication validation. MaxPlayers is 16 total (host plus up to 15 remote clients). For CPU comparisons, run clients on a different machine and hold build, network settings and workload counts constant. Logs and the HUD update at phase boundaries; client digests run after the workload, outside future timing intervals.

Expected: all required clients match settled state and the HUD shows PASS. Host-only runs show COMPLETE (host only). A failed check or timeout shows FAIL. No frame/tick timing, bandwidth, allocations, GC or profiling results have been collected.

## Validation

Run `python game/unittest/addons/networkobjectload/verify.py` from the engine checkout. It validates scene/settings JSON, GUID uniqueness and required components, then compiles only addon code against existing local managed assemblies with the engine code generators. Generated verification output is ignored. Actual check status is recorded in `workloads.json`; local compilation does not establish editor sandbox or multiplayer success.
