# Editor Slowdown Repro

A small s&box project for editor state that keeps growing over a long session, related to [sbox-public#1207](https://github.com/Facepunch/sbox-public/issues/1207).

## Reproduce

1. Open editorslowdown/.sbproj in s&box and open `scenes/editor-slowdown.scene`.
2. Run `slowdown_probe` in the console and note the line.
3. Undo history: run `slowdown_clutter 1000`, then `slowdown_undo 200` a few times. Open the Undo dock and use **Clear History**, then run `slowdown_probe` again.
4. Menu refresh: run `slowdown_refresh 20` a few times.
5. Mesh selection: select the **Target** object, switch to the **Mapping** tool with **Object Selection** active, then press Play and Stop (F5) ten times. Run `slowdown_probe` after each few cycles.

The scene has a sun, a camera and one box called **Target**. The project's `Editor/` code adds the console commands. `slowdown_clutter` adds plain cubes so each scene snapshot is bigger. `slowdown_undo` adds and removes a point light on Target through the normal undo scopes. `slowdown_refresh` runs the editor `refresh` event that follows every hotload. Each command ends with a `[Slowdown]` probe line: undo entries, the editor session's undo/redo callback counts, `MenuBar` cleanup callbacks, managed and private memory after a full GC, and the number of assembly load contexts.

What happens:

- Undo entries grow by two per pair and are never trimmed. Every removal stores a serialized copy of the whole scene (`SceneEditorSession.Undo.cs`), so managed memory climbs with each `slowdown_undo` and only drops after **Clear History**.
- `menu cleanup callbacks` grows by the number of `[Menu]` commands on every refresh. `MenuBar.Unregister` is invoked but never reset (`MenuBar.Registry.cs`), so every refresh also re-runs all the old cleanup closures.
- `undo callbacks` and `redo callbacks` go up by one per Play/Stop. The selection tool unsubscribes from the game session's undo system instead of the editor's (`SelectionTool.cs`), so every old selector stays alive and runs on each undo/redo.

Expected: the probe numbers level off. Undo history is capped or trimmed, refreshing replaces the menu callbacks instead of adding to them, and Play/Stop leaves the callback counts unchanged.

## Verified result

Local build of `origin/master` (`9ee2089ac`), 2026-10-26:

| Step | Probe |
|---|---|
| Baseline | 0 undo entries, 1 undo callback, 82 menu cleanup callbacks, 1153 MB managed |
| `slowdown_clutter 1000`, then `slowdown_undo 200` ×3 | 400 / 800 / 1200 undo entries; 2100 / 3043 / 3987 MB managed (about 4.7 MB per removal) |
| Clear History | 0 undo entries, 1161 MB managed |
| `slowdown_refresh 20` ×3 | 902 / 1722 / 2542 menu cleanup callbacks (+41 per refresh); each batch took about 780 ms, no visible time growth at this size |
| Object Selection on, Play/Stop ×1 / ×5 / ×10 | 3 / 7 / 12 undo and redo callbacks; after 10 cycles the editor undo system holds 11 `ObjectSelection` subscribers |
