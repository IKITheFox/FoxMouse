# v0.1 support matrix

| Area | Tier 1 | Fallback / not supported |
|---|---|---|
| OS | Supported Windows 11 x64 | Windows 10 and ARM64 are preview candidates |
| Desktop | Normal interactive desktop | Secure desktop, lock/login screen |
| Cursor | Standard static color cursors | XOR, unsupported ANI, app-drawn cursors |
| Display | 1–4 monitors, mixed DPI, 60–240 Hz | Topology transition temporarily restores native cursor |
| Apps | Windowed and borderless desktop apps | Exclusive full-screen and anti-cheat games |
| Remote | Local console session | RDP/VM uses compatible mode or disables effect |
| Accessibility | Standard pointer themes and sizes | Windows Magnifier forces compatible mode |
| Settings UI | Windows 11 x64 WinUI 3, system Light/Dark/High Contrast | WinForms fallback if the external settings host cannot launch |

Every unsupported case must preserve the visible native cursor.
