# FoxMouse v0.4 known limitations

- Tier 1 support is Windows 11 x64 on a normal local interactive desktop.
- Secure desktop, sign-in, lock screen and UAC prompts are not drawn over.
- The v0.4 overlay reasserts topmost z-order after each presented frame, but it
  cannot and must not bypass Windows secure-desktop isolation. Applications
  rendering their cursor inside protected or exclusive surfaces may therefore
  remain above or outside FoxMouse's normal-desktop overlay.
- RDP, virtual machines, Windows Magnifier and exclusive full-screen apps keep
  the native cursor unchanged in high-fidelity mode.
- App-drawn cursors and legacy XOR monochrome cursors cannot be captured safely;
  high-fidelity mode leaves them unchanged and retries after a normal cursor
  returns. Animated cursors are represented by the frame exposed by Windows.
- The layered-window backend is DWM-composited and reuses a small CPU DIB. A
  high-resolution cursor resource remains sharp on an 8K desktop, but a custom
  cursor that exposes only a low-resolution bitmap cannot be made objectively
  lossless; its diagnostic quality is reported as a scaled fallback.
- Negative coordinates, 100%–300% DPI, representative 1–4-monitor extents and a
  768×768 high-resolution fixture are covered deterministically. Physical
  2–4-monitor, HDR and mixed-refresh certification still requires the checked-in
  interactive matrix.
- Buffered Raw Input and same-millisecond timing are validated at an 8 kHz
  cadence. Vendor-specific touchpads still require physical false-positive and
  feel testing.
- Runtime faults automatically recover and Guard loss is fail-open, but no
  user-mode program can promise literal permanent availability across process
  termination, Windows secure desktops, driver/GPU failure, power loss, or OS
  session teardown.
- Guard shutdown deliberately keeps retrying restoration while it still owns a
  hidden cursor. This fail-open priority avoids abandoning a hidden cursor, but
  shutdown can wait indefinitely if Windows itself never confirms restoration;
  terminating the session or process remains an external recovery boundary.
- macOS does not publish its detector thresholds or animation curve. FoxMouse
  uses public demonstrations as an initial reference and requires calibrated
  HID replay/video measurements for a specified Mac build before claiming
  perceptual parity.
- Production Authenticode signing requires the publisher's trusted certificate;
  unsigned or development-signed builds may show Windows reputation warnings.
- The WinUI 3 settings host is a separate x64 process. If it is missing or
  cannot start, FoxMouse intentionally opens the simpler WinForms fallback;
  this does not affect the tray engine or Guard safety behavior.
- The running-app picker scans only the current login session. Protected or
  short-lived processes may have no readable title, path, or icon and can be
  omitted; Browse EXE remains available for applications that are not listed.
- Preview requires the tray host to be running. Unsaved controls are first
  atomically saved and hot-reloaded, so Preview demonstrates the visible form
  values; if the host is absent, the settings remain saved for the next launch.
