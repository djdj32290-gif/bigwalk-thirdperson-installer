# Third Person Comfort

A third-person camera mod built specifically for players who get motion sick in
first person. It puts the camera over your shoulder, **smooths out head bob**,
and shows your full character — head, body, legs and all.

![Third person view in Big Walk](https://raw.githubusercontent.com/Ranats/bigwalk-third-person-comfort/main/docs/preview.gif)

Unlike a general-purpose camera tool, every default here is tuned for comfort:
stable horizon-aware follow, no input hijacking, and the game's own look
controls stay untouched.

## Features

* **Third person** — over-shoulder camera showing your whole character.
* **Head-bob stabilisation** — the camera pivot is smoothed so the high-frequency
  bounce of walking is removed while real movement (crouching, riding,
  cutscenes) still comes through. On by default.
* **Optional horizon levelling** — removes camera roll entirely for players who
  need a dead-level horizon.
* **Wall-aware boom** — the camera pulls in automatically when geometry blocks
  the view. Uses whichever physics query this build actually ships (probed at
  runtime), so it never freezes your game on stripped IL2CPP methods.
* **Zoom** — mouse wheel adjusts the distance between your configured min/max.
* **Co-op safe** — acts only on your Mirror local player; what other players see
  is untouched. Client-side cosmetic only.
* **Status indicator** — small bottom-left label shows the mode and zoom
  distance (can be hidden).

## Controls

| Action | Key |
| --- | --- |
| Toggle third person | `F4` |
| Zoom in / out | Mouse wheel |
| Dump player hierarchy to log (troubleshooting) | `F8` |

All keys rebindable in `BepInEx/config/dev.sopur.bigwalk.thirdperson.cfg`.

## Installation

With a mod manager (r2modman, Gale), install and let it pull BepInEx in.

Manually: install **BepInEx 6 IL2CPP (win-x64)** — Big Walk is an IL2CPP game,
BepInEx 5 will not work — run the game once so it unpacks, then extract this
package so `BigWalkThirdPerson.dll` lands in `BepInEx/plugins/`.

On Steam Deck / Linux add to Launch Options:
`WINEDLLOVERRIDES="winhttp=n,b" %command%`

## Configuration

`BepInEx/config/dev.sopur.bigwalk.thirdperson.cfg`:

* `StartEnabled` (true) — enter third person automatically on world load.
* `Distance` / `MinDistance` / `MaxDistance` — boom length and wheel range.
* `Height` / `Shoulder` — pivot height and over-shoulder offset.
* `Collision` / `CollisionRadius` — wall avoidance.
* `Stabilize` (true), `StabilizeAmount` (0.85), `StabilizeSmoothTime` (0.12s) —
  head-bob removal. Lower `Amount` if the camera feels too detached.
* `LevelHorizon` (false) — force the camera level (removes roll).
* `AimConverge` (true), `AimSmoothTime` (0.08s), `InteractDistance` (0.1m),
  `InteractZoomTime` (0.3s), `AimMaxAngle` (20°) — when the game highlights
  an interactable the camera leans in to nearly first person (boom collapses
  to `InteractDistance`, offsets ease to zero, head+torso hide) so the game's
  own aim ray runs through the screen centre, then returns to third person
  when the target clears. Fixes the crosshair/interact-point offset.
* `CastFromCamera` (false, experimental) — reroute the local player's
  interact raycast through the third-person camera so the thing under the
  screen centre is literally what you interact with. Currently off by
  default: the game's caster also feeds reach/hand checks, and substituting
  camera hits there can destabilise the character.
* `ShowBody` (true) — draw your full remote body; `HideHead` hides the head if
  it ever blocks your view.
* `Mode` — `Direct` (default, recommended) or `Guide` (experimental).
* `ShowStatus` — the little status label.
* `ShowAimDot` (true) — a small on-screen dot marking where the game is
  actually aiming (the highlighted target, or the character's eye ray when
  nothing is targeted). Doubles as a diagnostic for `CastFromCamera`.
* `ToggleKey`, `DumpKey` — keybinds (any `KeyCode` name).

## Notes

* Cutscenes and scripted camera moments take priority in `Guide` mode; in
  `Direct` mode the mod re-applies the boom every frame, so if a cinematic looks
  wrong just tap `F4`.
* If a game update renames game classes, the mod fails soft: it logs a warning
  and stays in first person instead of crashing.

## Uninstall

Delete `BigWalkThirdPerson.dll` from `BepInEx/plugins`. The mod never writes to
your save.

## Support

Found a problem? Press `F8` in game and attach `BepInEx/LogOutput.log`.

If this saved your stomach, you can buy me a coffee:
[GitHub Sponsors](https://github.com/sponsors/Ranats) ·
[Ko-fi](https://ko-fi.com/ranats)

## Build from source

Requires the .NET SDK and a local Big Walk install with BepInEx 6 IL2CPP
already run once (so `BepInEx/interop/` exists). Then:

```
dotnet build -c Release -p:GameDir="C:\...\Big Walk"
```

(or set the `BIGWALK_DIR` environment variable). Drop
`bin/Release/net6.0/BigWalkThirdPerson.dll` into `BepInEx/plugins/`.
