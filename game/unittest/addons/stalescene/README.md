# Stale Scene Reference Repro

Minimal project for reproducing [sbox-public#10801](https://github.com/Facepunch/sbox-public/issues/10801).

## Repro steps

1. Open `stalescene/.sbproj` in s&box and let its code compile.
2. Open **Project Settings > Systems > Global**. Set **Stale Scene Repro System > World** to `Assets/scenes/system.scene` and save the project settings.
3. Open `Assets/scenes/system.scene`, add a visible object, and save the scene. To exercise the same-path branch directly, use **Save As** and select the same scene path.
4. Return to the main menu and click **Play**. The project opens `scenes/start.scene`; `StaleSceneReproSystem` then loads its global **World** reference.
5. Check the console for `Stale scene repro: loaded scenes/system.scene` and confirm your new object appears. A missing or invalid reference is logged as an error.

Expected: saving the scene updates the resource held by the global system setting. Play loads the
latest scene through that reference, including the new object.
