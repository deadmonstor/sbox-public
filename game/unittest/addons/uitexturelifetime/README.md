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
