# Scene Runtime Identity Repro

Repro for [sbox-public #12018](https://github.com/Facepunch/sbox-public/issues/12018), targeting `fix-scene-runtime-identity` (`6850d66c`).

## Reproduce

1. Open sceneruntimeidentity/.sbproj in s&box.
2. Open `Assets/scenes/level.scene`. It includes one static cube named **Bake trigger**.
3. Save the level, then use **Scene > Compile Scene** (F9). Wait for the compile to finish successfully. Ordinary asset compilation is not enough: this must create the level's scene bake.
4. With the level tab selected and Play stopped, run `identity_repro_dirty` in the console. It assigns a fresh `unsaved-xxxxxxxx` marker and explicitly marks the editor session dirty. Leave this change unsaved and keep the level editor tab open. Do not compile the scene again. If the command is unknown after adding this helper to an already-open project, reopen the project once to load its `Editor/` code.
5. Select the `menu.scene` tab, press Play, and click **Load level.scene**.
6. Read the panel and the `[Identity repro]` console line. The marker must match the fresh value logged by the command, proving the editor snapshot was used.
7. For the control, stop Play, close the level tab and discard only the marker change. Play the menu and load the level again.

The scene bake is essential. Without a bake, the loader can return the ordinary scene resource before it reaches the editor snapshot fallback, so both tab-open and tab-closed runs can pass. The static cube gives the scene compiler something to bake.

## Verified result

On Josh's Steam editor, version `26.10.02`, on 2026-10-03:

- Baked level, open tab, unsaved marker edit: **FAIL**. `Scene.Name` remained `menu`; source name and path were empty; resource reference was null; marker was `unsaved`.
- Same baked level with its editor tab closed: **PASS**. Name was `level`, path was `scenes/level.scene`, and the asset reference matched.
- Original project with no scene bake: both saved-tab runs passed. That setup did not exercise the fix.

The report also describes saved scenes. An edit saved after a bake can still leave compilation dirty; saving and compiling are separate operations. The unsaved-marker sequence above is the confirmed failing case. The exact saved-tab sequence has not yet been verified here.

## Before and after

Use the same project and steps on binaries without and with the fix. For an exact patch comparison, the commits are `6850d66c^` and `6850d66c`. Restart the editor after changing engine builds; switching git branches does not change the running editor's binaries.

Before the fix: the open dirty-tab load loses its source identity. After the fix: it should preserve the level name, source path and asset reference while showing the unsaved marker. The fixed-build result has not been run in this verification.

Generated bake data is local and excluded from git. Repeat the bake step on each fresh checkout/build; do not delete or fabricate compilation manifests to trigger the bug.
