# ScreenDim

**Turn the physical monitor backlight off via DDC/CI — while Windows keeps rendering, and screenshot / OCR / Desktop Duplication keep working.**

Most ways of "dimming" a Windows display break screen capture. ScreenDim does not.

| Approach | What actually happens | Capture still works? |
|---|---|---|
| Software overlay (black full-screen window) | Covers the pixels with a black layer | ❌ No — you capture the overlay |
| `SetDeviceGammaRamp` (gamma ramp) | Changes GPU output state | ⚠️ Inconsistent — some APIs see black, some don't |
| Windows power plan "turn off display" | The GPU output link actually goes down | ❌ No — black frames, changed desktop size |
| DDC/CI **power off** (VCP 0xD6 = 4) | Monitor enters DDC power-off | ❌ Often unrecoverable via DDC; link may drop |
| **ScreenDim** — DDC/CI **backlight = 0** (VCP 0x10) | Only the monitor's internal backlight register changes | ✅ **Yes — bit-identical pixels** |

The last row is the whole point. DDC/CI writes travel over the I²C/AUX channel to the monitor's
internal backlight register. The frame buffer, DWM composition and GPU scanout are untouched — they
are two completely separate paths:

```
        ┌──────────────── GPU ────────────────┐
 Apps → │ DWM compose → framebuffer → scanout ┼──→ monitor scaler ──→ panel
        └─────────────────────────────────────┘            ↑
                                                           │  DDC/CI (I²C / AUX)
             ScreenDim ────────────────────────────────────┘  backlight register only
```

Every capture path — GDI `BitBlt` / `PrintScreen`, DXGI Desktop Duplication, `Windows.Graphics.Capture` —
reads the **DWM-composited frame buffer**. None of them touches the panel backlight. So they get the
exact same pixels whether the panel is lit or dark.

ScreenDim also calls `SetThreadExecutionState(ES_DISPLAY_REQUIRED)` to **prevent** Windows from
powering the display down, sleeping, starting the screensaver or auto-locking. Windows always believes
the display is on; ScreenDim extinguishes the backlight itself.

Optionally it drops both displays to a lower refresh rate while dark — invisible to the user, but it
saves real GPU power (numbers below).

---

## Quick start

Requirements: Windows 10/11 and .NET Framework 4.x (ships with Windows — nothing to install).
DDC/CI must be enabled on your monitor (usually on by default; sometimes a setting in the OSD menu).

```powershell
git clone https://github.com/SuperJufufu/screendim
cd screendim
.\install.ps1          # adds a startup shortcut and starts the daemon — no admin rights needed
```

Default behaviour: after **60 s idle**, the backlight goes to 0 on all displays. Any real mouse
movement (> 4 px net), click or scroll restores it in ~0.47 s.

Verify it works on *your* hardware before trusting it:

```powershell
.\ScreenDimCLI.exe --list        # which displays respond to DDC/CI, and their current values
.\ScreenDimCLI.exe --selftest    # dims for ~8 s and proves capture still receives real pixels
```

`--selftest` never fakes a pass: if fewer than 1 % of pixels are static (e.g. you are playing video),
or if the panel never actually got darker, it reports **INCONCLUSIVE** instead of OK.

Common commands:

```powershell
.\ScreenDimCLI.exe --status       # is it currently dark?
.\ScreenDimCLI.exe --dim-now      # go dark immediately (+ 60 Hz + power saver plan)
.\ScreenDimCLI.exe --wake-now     # restore immediately
.\ScreenDimCLI.exe --restore      # restore from crash records
.\ScreenDimCLI.exe --stop         # stop the daemon (restores before exiting)
.\uninstall.ps1                   # stop, restore, remove autostart
```

---

## What the self-test actually measures

A naive test ("take a screenshot while dark — is it black?") can pass for the wrong reason.
ScreenDim uses a **witness-pixel** method:

1. While the panels are **lit**, capture two consecutive frames and find the pixels that do not change
   between them — millions of pixels are perfectly static.
2. Extinguish the backlight and capture again.
3. If dimming interfered with capture, those witness pixels should now be nearly 100 % black.

Measured on the author's machine (RTX 5070 Ti, HKC G27H2 + Philips 27M4N5500PT, 5120×1440):

| Run | Static witness pixels | Changed when the panels went dark |
|---|---|---|
| 1 | 77.9 % of screen | 2.667 % |
| 2 | 94.9 % of screen | 0.457 % |
| 3 | 96.4 % of screen | 1.206 % |
| 4 | 98.7 % of screen | **0.165 %** |
| 5 | 98.8 % of screen | 0.335 % |

Only a fraction of a percent changes — and the strongest control is that the *animated* share is
essentially identical lit vs. dark (run 3: 3.618 % lit vs. 3.631 % dark). That residual motion is an
animated window still repainting, not the dimming.

Captured resolution and PNG size are unchanged across states (2560×1440; 3526 / 3529 / 3524 KB).

---

## Refresh-rate drop (optional power saving)

On a high-refresh dual-monitor setup left on 24/7, dropping to 60 Hz while idle is free power.
Measured on 180 Hz + 260 Hz panels:

| State | GPU power | Delta |
|---|---|---|
| 180/260 Hz, backlight normal | 58.5 W | — |
| 60/60 Hz, backlight normal | 39.3 W | **−19.2 W** |

That 19.2 W is on top of whatever the backlight itself saves (the panels' own T-con/scaler draw),
and it is not counted in the GPU figure.

Refresh rate changes only **timings** — never resolution or colour depth — so captured frames are
geometrically identical (verified with ffmpeg `ddagrab` / DXGI Desktop Duplication).

The change is session-only: **your default display mode in the registry is never modified.**

### Why the wake order matters

Restore order is deliberately "backlight first, then refresh rate". Measured: after
`ChangeDisplaySettingsEx` returns, the DDC channel needs ~70 ms, and a DDC write issued too early gets
blocked and retried.

| Wake order | Screen visible | Refresh restored | Feel |
|---|---|---|---|
| Refresh first | 3402 ms | same moment | 3.4 s staring at a black screen |
| **Backlight first (current)** | **467 ms** | 1437 ms | visible almost instantly, one resync flash afterwards |

Going *into* dark the order is reversed (backlight first, then refresh rate) because nobody is looking,
so the mode switch is invisible. Add `--no-hz` to avoid the resync flash and give up the 19 W.

---

## Parameters

| Flag | Default | Meaning |
|---|---|---|
| `--idle <s>` | 60 | Idle seconds before going dark |
| `--level <0-100>` | 0 | Backlight value while dark |
| `--hz <n>` | 60 | Refresh rate while dark; `0` or unsupported = leave alone |
| `--no-hz` | — | Never touch refresh rate (no wake flash, no 19 W saving) |
| `--saver-plan <p>` | saver | Power plan to switch to: `saver` / `balanced` / `high` / `ultimate` / `<GUID>` / name substring |
| `--no-plan` | — | Never touch the power plan |
| `--peek <sec>` | 15 | After a manual dim, seconds of light on mouse move; `0` = fully locked |
| `--mode dim\|off` | dim | `dim` = backlight only; `off` = DDC power off (**risky**, see below) |
| `--wake mouse\|any` | mouse | `any` = keyboard wakes it too |
| `--threshold <px>` | 4 | Net cursor movement that counts as "moved" |
| `--reassert <s>` | 20 | Periodically re-write the dim value against panel self-revert |
| `--restore-level <n>` | 70 | Fallback brightness if the original could not be read |
| `--allow-sleep` | off | Do not block sleep / display off |
| `--dry-run` | off | Log decisions without writing to the monitor |
| `--allow-power-off` | off | Permit `--mode off` to actually execute |

State files live in `%LOCALAPPDATA%\ScreenDim\`: `screendim.log`, `status.txt`, `state.txt` (backlight
restore record), `state-hz.txt`, `state-plan.txt`, `report.txt`, `shots\`.

---

## Not getting stuck on a black screen

This is the failure mode that matters, so it gets several independent layers:

1. **Restore records are written atomically *before* anything changes.** If the record cannot be
   written (disk full / locked), ScreenDim **refuses to dim** and logs why — better to lose the saving
   than to lose your original settings.
2. Records are deleted only after **every** registered display has been successfully written back.
   Unplug a monitor mid-session and its record is kept for the next restore attempt.
3. Every exit path — `stop`, `--max-seconds`, Ctrl+C, `AppDomain.ProcessExit` — **waits for the restore
   to finish** (up to 15 s) instead of truncating it. Non-zero exit code = restore incomplete.
4. A panel read-back that differs from the requested value (firmware clamping 0 to 10) is treated as
   "write issued, verification failed" — your original value is still restored, the record never dropped.
5. A DDC call that hangs past 4 s does **not** destroy the still-referenced display handle
   (dxva2 handles are small integers and get reused — destroying one can write to a *different*
   monitor). It leaks one handle instead.
6. **Single-instance mutex**: a second daemon exits immediately (code 2), so two instances cannot
   fight over backlight and refresh rate.
7. Manual escape hatch that does not need a visible screen:
   `ScreenDimCLI.exe --restore-saved` (same as `--restore`, works with the daemon stopped).

**Why `--mode off` is disabled by default:** `VCP 0xD6 = 4` puts the monitor into DDC power-off.
Darkest and most efficient in theory, but on some monitors the DDC channel also stops responding
(wake only via the physical buttons), and a few models make Windows believe the display was
disconnected — resolution changes, windows reflow, which breaks the entire "the picture is still there"
premise. `--mode dim` (backlight = 0) never touches the display link, so it is the safe default.

**Is backlight 0 truly black?** Depends on the panel. Most go essentially dark at `VCP 0x10 = 0`, some
retain a faint glow. Run `--selftest` and look at it before committing.

---

## Web control panel

`install.ps1` also drops a **Power Saver** shortcut that starts the daemon if needed and opens
`http://127.0.0.1:8765/`. It shows screen state (normal / saving, plus backlight and refresh rate),
CPU load from `GetSystemTimes` deltas, GPU load and GPU power via **NVML** (`nvml.dll`, shipped with
the driver — it does not spawn `nvidia-smi`), and memory usage.

Three buttons: **Power save** (toggle; the mouse briefly wakes it for `--peek` seconds so you can find
the page again), **5-minute auto** (enable/disable the idle trigger), and **Lock** (a full lock:
backlight 0 + 60 Hz + saver plan; neither mouse nor keyboard cancels it, and the backlight is
re-verified every 5 s). Unlock from the page, or with `ScreenDimCLI.exe --wake-now`.

The server is a hand-written minimal HTTP/1.1 listener on `127.0.0.1` (TcpListener, not `HttpListener`)
so it needs no `netsh http add urlacl` and no elevation. Page assets are embedded in the exe.

---

## Known hardware limit: the Philips 27M4N5500PT stores brightness per refresh-rate mode

This panel **acknowledges a DDC brightness write and does not apply it** until the display link
re-negotiates. Worse, switching back to the original refresh rate brings the old brightness back too.

Verified: write brightness 0 (reads back 0, screen stays lit) → change 275 → 60 Hz and hold → **screen
goes dark** → 275 → 260 → 275 → **lit again**. Re-issuing the same mode is a driver no-op and does not
trigger it.

So "brightness only, no refresh change" is impossible on this panel in hardware. That is why the
**Lock** button also drops to 60 Hz and stays there. Only the HKC panel responds to brightness alone.
On this machine `--no-hz` leaves the second screen lit — use with care.

---

## Build from source

```powershell
.\build.ps1
```

One source file, two binaries: `ScreenDim.exe` (`winexe`, never shows a console — the daemon) and
`ScreenDimCLI.exe` (console build for `--list` / `--selftest` / `--status`). Compiled with the
`csc.exe` shipped with .NET Framework 4.x; no extra runtime or SDK required.

> ⚠️ `ui.html` and the `.ps1` files must be saved as **UTF-8 with BOM**. PowerShell decodes a BOM-less
> `.ps1` as system ANSI (GBK on a Chinese system), which mangles non-ASCII text and can swallow quotes
> and break parsing. Hit in practice.

---

## Power-plan switching needs no admin rights

`powercfg /setactive` maps to `PowerSetActiveScheme(NULL, ...)`, which changes the **current user's**
active scheme (`ActivePowerScheme` under HKCU) rather than the scheme contents in HKLM. So it needs no
elevation. ScreenDim calls `powrprof.dll` directly instead of spawning `powercfg.exe`.

Verified on a non-elevated shell (`elevated=False`, UAC on): switching between Balanced / Saver /
Ultimate all succeeded.

> ⚠️ The **Saver** plan caps CPU frequency and core parking, so a background screenshot / OCR workload
> is throttled while saving power. It still works, just slower. Use `--saver-plan balanced` for a
> gentler option.

**Does dimming break anything else?** ScreenDim does not modify the system power plan; it blocks
display-off via `ES_DISPLAY_REQUIRED`, which reverts the moment the process exits.

---

## License

MIT — see [LICENSE](LICENSE).
