# UI Texture Lifetime Repro

Diagnostic project for the report of directly assigned UI textures disappearing during play on staging. No issue number supplied; the disappearance is not confirmed in this project yet.

## Reproduce

1. Open uitexturelifetime/.sbproj in s&box.
2. Start the scene on staging and leave it running without changing files or hotloading.
3. Watch the six checkerboard images. Note which row and column disappears, the elapsed time, and the console output.
4. If an image disappears in the editor, select Texture Lifetime UI and inspect its Png/Vtex properties. Note whether the image returns.
5. In a separate run, enable MarkTexturesUsed on Texture Lifetime UI to test whether explicitly reporting streaming usage prevents disappearance.
6. Publish the project and repeat the idle run to compare with the editor.

The scene compares serialized Texture properties, Texture.Load assigned directly, and Image.SetTexture(path), for both a PNG and a VTEX compiled from that PNG. Image indices in logs are 0/1, 2/3, and 4/5 respectively (PNG/VTEX). It reports validity changes immediately and validity, error state and LastUsed every 30 seconds. It never refreshes or reassigns images after setup. The routes may share cached texture wrappers, matching normal engine behavior.

Expected: all six images remain visible. If a border remains but its checkerboard disappears while validity stays true, retain the logs for investigation of rendering/streaming. MarkTexturesUsed is disabled by default; leave it off for the baseline.

## Stress modes

Click Toggle all stress modes to start or stop the stress run. The inspector also exposes each mode separately so a failure can be narrowed down:

- CycleVisibility hides alternating columns for 20 seconds, followed by 10 seconds visible. Images hidden during this window are intentional; they should return afterwards.
- TextureChurn creates eight 128–512 pixel textures each second, explicitly disposing half and leaving half for normal garbage collection.
- PanelChurn replaces a strip of twelve temporary images each second, mixing generated textures with shared PNG/VTEX loads. Shared textures are never explicitly disposed.
- AllocationPressure allocates 2 MB each second and retains at most four buffers, encouraging normal garbage collection without calling GC.Collect.

The fourth row contains two generated checkerboards (orange/green), held only by their Image panels. They should stay visible despite churn elsewhere. Stress modes and MarkTexturesUsed start disabled. The cycle counter and modes are shown onscreen and logged every ten seconds; the random texture sizes use a fixed seed for repeatability. Stop stress to restore all hidden images, then inspect/log any that remain missing. Try each mode separately before combining them. Do not edit files while testing.
