# Cloned Rigidbody MassOverride Repro

A self-contained repro for [sbox-public#10958](https://github.com/Facepunch/sbox-public/issues/10958).

## Reproduce

1. Open `massoverride/massoverride.sbproj` in s&box.
2. Open `Assets/scenes/minimal.scene` and press Play.
3. Read the expected and actual masses in the game view or the `[MassOverride]` console messages.

The scene creates disabled source objects with a ModelRenderer, ModelCollider and Rigidbody, clones them, then enables the clones. It generates both physics models locally; no cloud models are required. One model has a single 40 kg part and a 1500 kg override. The other has 40/4/4 kg parts and a 40 kg override. Gravity is disabled, but the rigidbodies remain dynamic.

Expected: the clones weigh 1500 kg and 40 kg respectively. The component reads the masses on the second update without modifying either clone's mass after activation.

## Verified

Reproduced in the editor on 2026-10-29, built with `Setup.bat` at upstream commit `3e2f34507`: the single-part clone reports 40 kg instead of 1500 kg, and the three-part clone reports 4 kg instead of 40 kg. Both physics bodies are dynamic. The result repeats after stopping and restarting play.
