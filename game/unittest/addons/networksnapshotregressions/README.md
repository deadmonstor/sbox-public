# Network Snapshot Regressions

Three separate scenes for regressions in [347523ac](https://github.com/Facepunch/sbox-public/commit/347523ac6f4f1949d19adf58928ac456a0e582fd) (snapshot ordering and ownership) and [ec0fa31](https://github.com/Facepunch/sbox-public/commit/ec0fa31fdfbae4b31f9b92b6221aedca86020593) (typed RPCs).

## Reproduce

1. Open `networksnapshotregressions/.sbproj` in s&box.
2. Open one of the scenes below and press Play. Each scene starts a lobby and shows its instructions and observed state on a HUD.
3. For multiplayer scenes, use the network status menu's **Join via new instance**. Join clients before starting a scenario. Stop Play before changing scenes; restart the clients for the new session.

The scenes contain a camera, a lobby helper, a controller and a text HUD. Multiplayer actors are spawned at runtime. No external assets are needed.

Before testing, confirm the HUD shows the scene's numbered title (`01`, `02` or `03`) and host/client role. The HUD queries the controller's `IReproStatus` interface directly; the scene index does not include the base `Component` type.

## 01 — Host-only hotload desync

Open `Assets/scenes/01-hotload-desync.scene`. Join one client and wait for the warmup to finish and both HUDs to show the settled value `239`.

In the host editor, choose **Repros > Network snapshots > Hotload host scene**. The editor helper invokes the host's real `Scene.OnHotload()` lifecycle, then changes the synced value once to `999`. An independent RPC announces the expected value.

On the affected build, the host shows `999` while the client keeps `239`, even though its RPC expected value is `999`. Leaving the value unchanged does not repair it.

Expected: the client receives `999` after hotload.

The editor helper deliberately hotloads only the host scene. Ordinary source edits are mirrored to connected clients and reset their receive history too, masking this case. The helper uses reflection only in the trusted editor project; game code does not access internal networking APIs.

## 02 — Refresh / takeover race

Open `Assets/scenes/02-refresh-takeover.scene`. Join two clients. In the console of **every instance**, set `net_fakelag 100`, then allow the connections and actor to settle. The HUD identifies owner A and contender B.

Press **Space on the host** to schedule one race: A refreshes its object twice, then B takes ownership 50 ms later, before those refreshes arrive. Every peer subsequently reports its observed owner through RPCs rather than through snapshot state.

On the affected build, the owner reports disagree: A retains A while B considers itself the owner. The host's result depends on message ordering. The HUD reports what each peer actually observes; it does not assume which owner wins.

Expected: every peer converges on the same owner.

Reset `net_fakelag 0` in every instance when finished. Restart the scene to run again. The artificial lag is required to expose the stale-version window reliably; the repro does not change transport or snapshot internals.

## 03 — Host-local RPC serialization

Open `Assets/scenes/03-local-rpc.scene`. No clients are needed. Wait for the lobby, then press **Space on the host**.

The controller calls two `[Rpc.Host]` instance methods: an integer control and a callback taking `System.Action`. Both target the current host, so neither needs a network send. The callback is intentionally not a supported network argument; this isolates the old self-target fast path rather than testing remote delegate serialization.

On the affected build, the integer body runs but the callback throws `NotSupportedException: Unhandled type System.Action` before its body runs. The HUD shows attempts, both body counts and the exception.

Expected: self-targeted calls bypass serialization and both bodies run, as before the typed-RPC change. Do not invoke the callback RPC from a client.
