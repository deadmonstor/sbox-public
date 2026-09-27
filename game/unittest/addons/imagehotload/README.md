# Image Hotload Demo

A small s&box game project for recording a local image hotload. It shows the live texture beside the after image.

## Run it

Open `game/unittest/addons/imagehotload/.sbproj` in s&box and start the game.

## Show the update

While the game is running, copy `Assets/textures/hotload-after.png` over `Assets/textures/hotload.png`. The left image should update to match the right one. Copy `Assets/textures/hotload-before.png` over `hotload.png` to reset.

From the project folder, PowerShell can do the swap with:

```powershell
Copy-Item Assets/textures/hotload-after.png Assets/textures/hotload.png -Force
```
