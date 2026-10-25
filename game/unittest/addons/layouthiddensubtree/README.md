# Layout Hidden Subtree Test

Before and after project for skipping the re-zeroing of unchanged `display: none` subtrees on every layout pass (branch `perf/layout-hidden-subtree-zeroing`, commit `d698e48bc`). There's no issue for this change.

## Reproduce

1. Open layouthiddensubtree/.sbproj in s&box built from master (`86f70929f`).
2. Start the scene. Wait for the checks to finish, then click Benchmark and wait about 10 seconds for the result line.
3. Build the engine from `perf/layout-hidden-subtree-zeroing`, restart the editor and repeat step 2.

The scene has one screen panel. A blue bar is resized every frame so the root layout runs every frame, like an animated HUD. Next to the bar is a `display: none` container holding 0 to 25,000 small panels; the buttons change the count. The stats line shows the root's `RootPanel.Stats.LayoutTime` averaged over the last 120 frames. Benchmark measures each count in turn (30 warmup frames, then 120 samples) and logs a `HiddenSubtree` line with the results.

Run checks turns off the per-frame resize so only the changes under test trigger layout. It hides and shows a panel, moves a laid out panel into a hidden subtree, adds and resizes a panel while it's hidden, then shows and hides the subtree again. It prints PASS/FAIL with the actual and expected sizes. The checks also run once on start.

On master the layout time grows with the number of hidden panels, because the whole hidden subtree is zeroed on every pass.

Expected: on the fix branch the layout time is about the same for every count, and all checks pass on both builds.
