# Main Camera Toggle Repro

A small s&box project for [sbox-public#11402](https://github.com/Facepunch/sbox-public/issues/11402).

## Reproduce

1. Open maincameratoggle/.sbproj in s&box.
2. Start the game from the editor.
3. Check the Game View.

The scene has two cameras in hierarchy order. Main Camera is first and renders a green marker; Other Camera is below it and renders a red marker. Each camera excludes the other camera's marker.

On the affected build, the Game View should show the red marker from Other Camera. Expected: it shows the green marker from Main Camera.