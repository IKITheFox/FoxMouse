# Architecture decision record

## Runtime split

- `FoxMouse.App`: tray UI, Raw Input sink, detector, effect controller and overlay.
- `FoxMouse.Settings`: on-demand, single-instance WinUI 3 settings/About process.
  It shares the versioned settings contract with the tray host and is not part
  of the input, rendering, or Guard safety path.
- `FoxMouse.Guard`: independent Magnification API visibility watchdog.
- `FoxMouse.Deployment`: shared transactional install, repair, upgrade,
  uninstall, rollback, ARP, shortcut, and safe-process-control implementation.
- `FoxMouse.Setup`: self-contained graphical setup that embeds the exact
  portable package.
- `FoxMouse.Uninstall`: graphical maintenance entry stored in the install root;
  Windows Installed Apps uses the same maintenance contract.
- `FoxMouse.Cleanup`: a small, constrained .NET Framework helper that waits for
  a verified maintenance host to exit, deletes only the approved ordinary file
  and its now-empty directory, then terminates without a reboot-delete entry.
- `FoxMouse.Core`: platform-free deterministic algorithms and trace model.
- `FoxMouse.TraceTool`: fixture generation and deterministic replay verification.

## Rendering choice for v0.1

The v0.1 backend uses a small per-pixel-alpha Win32 layered window. Cursor
rasterization is CPU-side and final composition is performed by DWM. Only the
cursor-sized surface is updated, and no frames are produced while idle. This is
available in the installed .NET Windows Desktop toolchain and can be measured on
the target machine. A DirectComposition backend remains replaceable behind the
overlay interface if measured frame-time gates are not met.

## Data flow

```text
WM_INPUT -> InputNormalizer -> ShakeDetector -> EffectStateMachine
GetCursorInfo -> CursorRasterizer ------------> LayeredCursorOverlay
Policy ---------------------------------------> EffectController
EffectController <-> Guard named pipe --------> MagShowSystemCursor
Show acknowledgement -> remove overlay ------> bounded WM_SETCURSOR refresh

FoxMouse.Settings -> atomic settings.json -> SettingsChange named pipe
                  -> FoxMouse.App UI queue -> ApplySettings / Preview
```

Core code has no Win32 dependency. Windows time and input are converted to
integer microseconds and milli-DIP before entering the detector.

The settings-change pipe is limited to the current Windows user. The tray host
validates the message version, command, and exact normalized settings path
before dispatching it to the UI thread. Persistence precedes notification, so a
missing or stopped tray host cannot corrupt or undo a settings save. Preview is
an ephemeral command and is ignored when the cursor host is not running.

## Deployment flow

```text
Setup / Installed Apps / root maintenance EXE
        -> deployment mutex -> verified staged package
        -> Guard restore -> stop verified FoxMouse processes
        -> atomic directory activation -> shortcut + HKCU ARP
        -> commit, or restore files/cache/registry/shortcut/running state
```

The deployment engine is per-user and does not require elevation. It validates
archive paths and expansion budgets, binds the repair cache hash to the managed
install state, and performs operations from a controlled staging copy. Settings
are outside the program directory and are preserved on uninstall unless the
user explicitly requests deletion. A root maintenance executable first hands
off to a verified temporary host so its own installation directory can be
repaired or removed. A separately versioned cleanup companion waits for that
host and removes only a strict GUID temporary root or the fixed per-user
maintenance-host root; it never schedules a path for deletion after reboot.
