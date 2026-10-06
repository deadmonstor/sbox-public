# Clientside prediction test

A local multiplayer test for `feature/clientside-prediction`. Requires an engine build containing `PlayerController.UseClientPrediction`.

## Run

1. Open `clientsideprediction/.sbproj` in the s&box editor using the feature engine build.
2. Open `scenes/prediction-test.scene` and press Play. It creates a friends-only lobby and spawns a player for each connection.
3. Use the editor's multiplayer testing controls to join with a second client, or have a Steam friend join the host lobby. The joining process must join the existing lobby, rather than independently pressing Play and creating another host. For a console connection, use `connect <host lobby ID>`.
4. Test from the owning client. Testing only on the host does not exercise prediction over the network.
5. In the owning client's console, try `net_fakelag 100` and `net_fakepacketloss 10`. If the console reports cheats are required, enable `sv_cheats 1` on the host first. Reset both network values to `0` when finished.

The same running project compares prediction ON with the existing rigidbody controller OFF. It depends on the new API and will not compile against an engine built from master without this feature. Rebuild and restart the editor when changing engine builds.

## Controls and checks

The clickable controls appear when you start. Click **Play** to move and look around; hold **Tab** whenever you want the cursor and buttons back.

- WASD: move. Shift: run. Ctrl: crouch. Space: jump.
- **Toggle prediction** button on the host: switch prediction for every player. The HUD confirms the received mode.
- **Reset everyone** button on the host: teleport everyone back to spawn. Clients should reset their prediction timelines and converge to spawn.
- **Respawn me** button: ask the host to reset your own player.
- Green stairs ahead have 16-unit risers. Red walls test sliding and corners. Blue and purple slopes test walkable versus steep ground. The yellow tunnel has 44-unit clearance: crouch to enter, and standing must stay blocked beneath its roof. The pink platform translates and rotates; jump onto it and watch for carry corrections. The orange rigidbody moves through the spawn lane: stand in its path to compare pushes with prediction ON and OFF, on both the host and owning client.

With prediction ON, local movement responds immediately under latency. Host corrections restore authoritative collision state and replay pending commands; the HUD reports positional corrections. Remote players remain interpolated. Repeated jump input packets must not produce extra jumps. Turning prediction OFF restores rigidbody movement, with its different movement/physics behavior.

The HUD shows mode, host/client role, speed, grounded state, crouch state, and cumulative correction count. A rising count is diagnostic, not an automatic failure: moving-world collision queries can legitimately cause corrections. Watch for sustained drift, clipping, repeated jumps, or failure to recover after a teleport. This project does not test custom movement modes or native rigidbody rollback.

## Validation

Project and scene JSON and addon compilation are checked locally. Manual editor/multiplayer runs are left to the tester; no visual PASS claim is made without a run.
