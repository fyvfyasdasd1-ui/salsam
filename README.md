# BoneESP — GTA V Story Mode bone ESP & aim assist

A ScriptHookV-based ASI plugin that draws skeleton/bone ESP on nearby peds in
**GTA V Story Mode (singleplayer)** and provides an optional aim-snap to the
head bone of the closest hostile ped.

## SINGLEPLAYER ONLY

This mod is built on top of [ScriptHookV by Alexander Blade](http://www.dev-c.com/gtav/scripthookv/).
ScriptHookV **deliberately disables itself when GTA Online is active** — it
checks the game's network state and unloads its scripts. This plugin inherits
that guarantee, and also adds its own runtime guard
(`NETWORK::NETWORK_IS_SESSION_STARTED()`) that bails out if it ever finds
itself in an online session.

Do not try to remove these guards. The whole point of using ScriptHookV is
that it stays in singleplayer. If you want online — this isn't the project.

## What it does

- Enumerates nearby peds (`worldGetAllPeds`)
- For each ped, draws a 3D skeleton by reading bone world positions with
  `PED::GET_PED_BONE_COORDS` and connecting them with `GRAPHICS::DRAW_LINE`
- Draws a head marker, health bar, and distance label
- Color-codes peds: green = friendly, red = hostile, yellow = neutral, gray = dead
- Optional aim assist: when toggled on AND the player is free-aiming, smoothly
  rotates the gameplay camera so the crosshair tracks the head bone of the
  closest hostile ped within FOV. Operates against AI peds only — there are no
  other human players in Story Mode.

## Keybindings

| Key  | Action                            |
|------|-----------------------------------|
| F5   | Toggle ESP on/off                 |
| F6   | Toggle aim assist on/off          |
| F7   | Cycle ESP max distance (50/150/500m) |
| F8   | Toggle "friendly peds" filter     |

## Build

You need:

- Visual Studio 2019 or 2022 (Desktop development with C++)
- [ScriptHookV SDK](http://www.dev-c.com/gtav/scripthookv/) — download the SDK
  zip from Alexander Blade's site

Drop the SDK headers into `include/` and `ScriptHookV.lib` into `lib/`:

```
include/
  inc/
    enums.h
    main.h
    natives.h
    nativeCaller.h
    types.h
lib/
  ScriptHookV.lib
```

Then open `BoneESP.vcxproj` and build the **Release | x64** configuration.
The output `BoneESP.asi` lands in `bin/x64/Release/`.

## Install

1. Make sure you've already installed ScriptHookV — `ScriptHookV.dll` and
   `dinput8.dll` need to be in your GTA V install directory.
2. Copy `BoneESP.asi` next to `GTA5.exe`.
3. Launch the game in Story Mode. The plugin prints a notification when it
   loads.

## Notes

- The bone hashes used (`SKEL_Head` = 31086, etc.) are the standard GTA V ped
  skeleton bone IDs. They are documented in many open-source GTA V mods and in
  the natives reference at [alloc8or.re/gta5](https://alloc8or.re/gta5/nativedb/).
- World-to-screen for the head marker uses `GRAPHICS::_WORLD3D_TO_SCREEN2D`.
  3D skeleton lines use `GRAPHICS::DRAW_LINE`, which renders them as world-space
  lines that respect depth.
- Aim assist uses `CAM::_GET_GAMEPLAY_CAM_COORD` and
  `CAM::SET_GAMEPLAY_CAM_RELATIVE_PITCH/HEADING` for a smooth lerp toward the
  head bone, capped at a low max-speed so it feels like an aim assist rather
  than a snap.
