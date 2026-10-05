# Network Dormancy Lab

Ten saved correctness scenes for `delta-snapshots-dorment-objects`. No performance sampling or profiling is enabled.

## Run

1. Open `game/unittest/addons/networkdormancylab/.sbproj` in the s&box editor using the managed engine build for the dormancy branch.
2. Open `01-always-transmit` (the startup scene). Start one host and one client using the editor multiplayer test controls (or a client joining the host lobby). Keep the same lobby for the sequence.
3. Wait for the HUD to report each checkpoint. The controller waits for a connected client, creates the subjects, then performs two mutations with settling time between them.
4. A state pass requires every connected client to report matching enabled state, local position (rounded to one unit), networked parent GUID (scene parents are normalized), owner and the scenario's sync values. Missing reports and mismatches fail. A host-only run cannot pass.
5. For `08-late-join`, keep only the original client connected until **WAIT: start a second client now**. Join a second client at that prompt. Its current-state snapshot is checked before the second mutation; both clients then continue through scenes 9 and 10.
6. Check both host and client consoles for errors. In particular, `10-query-destruction` also requires **zero engine exceptions**; the state-match HUD cannot itself detect engine log errors.
7. After a pass, the host waits three seconds and loads the next scene using `Game.ChangeScene`, bringing every connected client along. A failure stops the sequence for inspection. Scene 10 ends the sequence. Stop Play and restart to reset.
8. To run one scene independently, open it and disable **AutoAdvance** on its Scenario Controller before Play. All saved scenes enable sequence mode by default.

The blue box is the subject. Orange boxes are parent roots or the query-destruction victim. A fixed camera, directional light and HUD are saved in every scene. Subjects are built deterministically by the controller so the scene files remain small.

## Required scenarios

| Scene | Pass condition |
|---|---|
| [01-always-transmit](Assets/scenes/01-always-transmit.scene) | AlwaysTransmit remains awake and sends two transform changes even when its visibility controller rejects the peer. |
| [02-visibility-wake](Assets/scenes/02-visibility-wake.scene) | After initially replicating, the subject becomes invisible, reaches delta dormancy, then becomes visible and sends its new transform. |
| [03-nested-transform-parent](Assets/scenes/03-nested-transform-parent.scene) | An independently networked child changes local position while acknowledged, then reparents between two networked roots. |
| [04-enabled-state](Assets/scenes/04-enabled-state.scene) | A fully acknowledged AlwaysTransmit object disables and re-enables, with both states observed by clients. |
| [05-scalar-sync](Assets/scenes/05-scalar-sync.scene) | Two scalar sync writes after settling each produce an independent client checkpoint. |
| [06-query-collections](Assets/scenes/06-query-collections.scene) | Backing-field query changes and in-place ordinary list/dictionary edits are detected twice without a wrapped property assignment. |
| [07-reliable-collections](Assets/scenes/07-reliable-collections.scene) | NetList and NetDictionary additions, clear/removal and replacement replicate after settling. |
| [08-late-join](Assets/scenes/08-late-join.scene) | Hide a subject from the first client until dormant; start a second client later and verify its snapshot before revealing a second mutation to both. |
| [09-ownership-transfer](Assets/scenes/09-ownership-transfer.scene) | Transfer an acknowledged object to a client and back; each new owner writes its scalar and transform. |
| [10-query-destruction](Assets/scenes/10-query-destruction.scene) | A clean object query destroys a second queued object, then itself; scene networking must continue without exceptions. |

## Scope and limits

These are end-to-end correctness scenarios, not bandwidth, latency or frame-cost benchmarks. The settle windows allow transport and interpolation to converge, so a state pass does not establish high-frequency replication performance. Query collections are mutated through their backing storage, not setters. Subjects are not refreshed between checkpoints; the ownership case uses an RPC only to ask the current owner to perform its write. Observation RPCs are sent on the always-transmitted coordinator rather than subjects.

The destruction case deliberately arms a getter without changing its return value. It exercises query polling or snapshot writing depending on pending acknowledgements; observations use backing fields so they do not trigger destruction themselves. Enabled state covers GameObject.Enabled; this project does not assume arbitrary component enabled changes replicate without a refresh.

Use the same scenes later when adding performance workloads. The manifest records instance counts, object populations, timing and current validation. Baseline build identity must be recorded separately when measurements begin.

## Validation

Local API compilation passed with zero warnings/errors. All ten scene JSON files and 123 unique scene/settings GUIDs validated. Josh ran all ten scenarios successfully on the dormancy branch with the snapshot-send destruction fix on 5 October 2026. A second client joined at the scene 8 prompt and both clients passed scenes 8, 9 and 10. The supplied host log contains no recurrence of the query-destruction exception; client consoles must also be checked. This is correctness coverage, with no performance measurements.

Rerun the scoped preparation checks with `python verify.py` (or `python verify.py --engine D:/sbox-public`). This uses existing managed assemblies and does not build the engine, launch the editor or collect performance data.

Automatic transitions and snapshot inclusion of camera/HUD are API-compiled and scene-chain validated; Josh's supplied multiplayer log confirms transitions through the complete sequence. Each new controller waits for an active client after the engine finishes its scene-load handshake.

This repro branch contains only the test project, based on `origin/master`. To compare behavior, run it against the dormancy engine branch with and without the snapshot-send destruction fix. Switching to this repro branch does not rebuild the engine.
