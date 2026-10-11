# D2RCameraTool

Diablo II: Resurrected — read and modify the camera angles and zoom in memory,
changing the camera perspective. A Cheat Engine table is included (`D2RCamera.CT`).

Original author: Discord `__shizza__`. This fork modernises the tool and makes camera
location version-independent.

> **DO NOT USE IN MULTIPLAYER OR YOU WILL CERTAINLY BE BANNED. FOR SINGLE PLAYER MODE USE ONLY.**
> D2R ships Warden anti-cheat — the loader module exports
> `g_warden_aegis_crash_callback_export`. Use offline single player only.

## Verified against D2R 3.0.91636

Tested live on build `3.0.91636` (offline single player), not just on synthetic targets.

**The camera constants did not change.** The 1.6-era 12-byte signature still matches the
real camera, byte for byte:

| field | value | notes |
|---|---|---|
| CamRoll | `0.099046` | |
| CamPitch | `-0.892399` | |
| CamHeight | `0.239118` | |
| CamYaw | `0.369644` | this is the view direction |

Found at two addresses 0x170 apart — the controller keeps both a current and a desired
camera state, and the whole structure is unmistakably a camera (adjacent fields include
world positions, a `1280 x 720` viewport, clip planes and a `0.707107 / 0.612372 /
0.866025 / 0.500000` rotation basis).

Zoom was located by the Cheat Engine pattern and read `0.5`; it lives at `pattern + 0x32`
with a usable range of roughly `(-0.25, 3.0)`.

All four angles and zoom were then confirmed by writing to them and restoring:

| write | observed |
|---|---|
| pitch `-0.892399` -> `-1.35` | view tilted sharply toward top-down |
| zoom `0.5` -> `2.0` | view pulled far back |
| yaw `0.369644` -> `+1.17` | world rotated about the player — **view direction changed** |

The written values read back unchanged in 114/114 samples during each hold, so the engine
*reads* these fields rather than re-asserting them from its own controller. That is why
they can be modified at all.

> Addresses are per-run (heap layout changes on every launch) — the signature is the
> stable thing, not the address.

### Note on running under a restricted host

If the tool reports `0 process(es)` and `win32 err 0`, the host is restricting process
visibility to its own job tree rather than denying access. Observed while developing under
a sandboxed agent harness: a native Python `CreateToolhelp32Snapshot` call saw 233 processes
and could open the game, while the same call from this .NET binary returned 0 for every
process outside the harness's own job. The tool now tries both the native Toolhelp32 walk and
the managed `Process.GetProcessesByName` and uses whichever finds the process. Running the
executable normally on a desktop is unaffected.


An earlier revision treated "no churn in the candidate's neighbourhood" as proof that a
signature hit was a stale copy, escalated to the structural scan, and scored frozen
candidates at `-1000`. On the live process this **returned the wrong address**: the player
was standing in the stash, so the camera never moved, and the real camera was demoted below
thousands of structural decoys. A static camera is normal. The frozen-means-dead penalty is
now applied only on the structural path, where a changed-constants camera really is expected
to be live.

## What changed in this fork

| | Before | Now |
|---|---|---|
| Target framework | `net6.0-windows10.0.17763.0` | `net8.0-windows` |
| Memory access | third-party `Memory.dll` (prebuilt, AV false positives) | own `Win32Memory.cs` P/Invoke layer, no binary dependency |
| Camera location | one hard-coded 12-byte AOB, broke on every game update | staged locator: exact signature → structural scan → temporal filter → churn ranking |
| Manifest | `requireAdministrator` (blocked the read-only locate path) | `asInvoker` + best-effort `SeDebugPrivilege` at runtime |
| Float writes | `float.ToString()` round trip (culture-dependent) | raw `BitConverter` bytes |
| Headless use | none | `--cli` mode writing a JSON report |

### Camera memory layout

Four consecutive `float`s; `base` is the address of **pitch**:

```
base - 4 : roll     (1.6-era default  0.099046)
base + 0 : pitch    (1.6-era default -0.892399)
base + 4 : height   (1.6-era default  0.239118)
base + 8 : yaw      (1.6-era default  0.369644)   <- this is the view direction
```

The original Cheat Engine table labels `+4` as `CamYaw` and `+8` as `CamHeight`; the
tool's own presets (`settings.ini`) disagree and are the correct assignment —
`+4` is height, `+8` is yaw.

The zoom value is a separate float, at `pattern + 0x32` from the zoom signature,
with a usable range of roughly `(-0.25, 2.0)`.

### Why the signature had to become adaptive

`D2R.exe`'s `.text` section is **encrypted at rest** (Shannon entropy 8.000 across the
whole section). The 1.6-era constants therefore cannot be found by scanning the file —
they only exist once `D2R_loader.dll` (`eidolon_run`, `.eid` payload) has decrypted the
code in memory. Any hard-coded signature is fragile, so the locator degrades gracefully:

- **Stage A** — the original exact 12-byte signature. Instant, and correct whenever the
  camera constants are unchanged.
- **Stage B** — structural scan for camera-shaped float quads over writable memory.
- **Stage C** — temporal filter: camera *angles* stay constant while the player moves.
- **Stage D** — ranking by how many fields are byte-identical to the legacy defaults,
  then by neighbourhood churn (a live game struct churns; stale byte copies do not).

## Building

Requires the .NET 8 SDK.

```
dotnet build -c Release
```

## Usage

### GUI

Run `D2RCameraTool.exe`. Enter the process name (`D2R`), wait for "Process found!",
then the tool locates the camera automatically. Drag with the pan hotkey (default
`Space`) to orbit, and the zoom hotkey (default `Z`) to zoom.

### WASD camera control

Hold the keys to swing the camera continuously, like a third-person game:

| key | effect |
|---|---|
| `A` / `D` | swing the view direction left / right (yaw) |
| `W` | look toward the ground (pitch down) |
| `S` | look toward the sky (pitch up) |

Movement is **time-based** (radians per second, `wasd_speed`, default `1.2`), so the feel
does not change with frame rate or with how often the UI loop happens to run. The keys are
read with `GetAsyncKeyState` rather than tracked from the keyboard hook, because a hook that
Windows silently removes after a timeout would leave a direction stuck on forever.

Movement only applies while the game owns the **foreground window** (`wasd_require_focus`),
so the camera cannot rotate while you are typing somewhere else. The panel shows whether the
game is currently considered focused — that is the first thing to check if nothing happens.

By default `wasd_block_keys` is on: while the game is focused, W/A/S/D are swallowed by the
keyboard hook so the game does not also act on them. Only those four keys, and only while
the game is focused.

**If an axis feels backwards, tick `Invert yaw` or `Invert pitch`.** I could not verify the
sign of yaw against the live game, so the default direction for A/D is a guess.

Limits: yaw is CLAMPED at ±π — it stops at the limit rather than wrapping round to the other
end, because the wrap read as the view snapping. Pitch is clamped to `[-1.55, +0.30]` rad
(almost straight down, to slightly above horizontal). Both live at the top of
`WasdCameraControl.cs` and are shared by the WASD keys and the mouse-drag path.

All six settings live in `settings.ini` and are written back when you press Save.

### Headless locate (diagnostics / re-deriving after a game update)

```
D2RCameraTool.exe --cli --process D2R --out d2rcamera_scan.json
D2RCameraTool.exe --cli --pid 12345 --out report.json      # explicit pid
D2RCameraTool.exe --cli --allow-write --out report.json    # open with write access
D2RCameraTool.exe --cli --allow-write --probe 0 --out report.json
```

CLI mode attaches **read-only** unless `--allow-write` is given. Exit codes:
`0` found, `1` not found, `2` could not open the process, `3` cancelled.

The JSON report lists the best candidate and the top 50, each with its
legacy-field match count, churn and score.

`--probe N` writes a visible pitch offset to candidate *N*, holds it for 1.5s, then
restores it. Watching the game view is the only conclusive test of which candidate is
the real camera — if the view tilts and snaps back, that candidate is it. Requires
`--allow-write`.

### What the stages produce on a hostile memory layout

Two synthetic targets are used to test the locator without the game:

| Scenario | Expected path | Result |
|---|---|---|
| Camera keeps the 1.6-era values, plus stale byte copies | Stage A, churn breaks the tie | finds the live one |
| Camera values changed, memory holds dead legacy-byte decoys | Stage A liveness check fails, escalate to Stage B | finds the changed-value camera |

The second scenario is the important one: it is what a version update looks like. The
legacy signature matches the decoys, and trusting it would return a dead address.

## Credits

- Original tool and Cheat Engine table: `ItsShiZZa`
- Original memory library (now removed): https://github.com/erfg12/memory.dll/
