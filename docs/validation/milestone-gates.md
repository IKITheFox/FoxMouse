# M0–M5 gates

| Gate | Required evidence |
|---|---|
| M0 | Product contract, support matrix, Mac web baseline, clean build, trace schema |
| M1 | Raw Input registration, cursor capture, layered overlay, fake and opt-in real Guard smoke |
| M2 | Positive/negative replay corpus, state animation, hotspot/DPI tests, deterministic soak |
| M3 | Tray/settings/startup/exclusions/logs, atomic config, graphical install/repair/uninstall |
| M4 | Fault injection, handle/memory checks, multi-monitor interactive checklist, performance report |
| M5 | Release build, portable archive, installer, SHA-256, privacy/limitations/changelog, final report |

M5 is a hard failure if any test can leave the native cursor hidden.

## v0.4.2 current release delta

The current gate target is v0.4.2. It retains all v0.4.1 recovery, Guard, input,
animation, 8K/mixed-DPI, soak, deployment, lifecycle and leak gates. In
addition, Setup and Maintenance must use mutually exclusive page states that
fit in one window. No state may expose a horizontal or vertical scrollbar,
wrap an action row, clip a visible control, or leave stale owner-drawn pixels
after focus, theme, DPI or state changes. The install, installed-maintenance and
uninstall-confirmation states each require a screenshot plus machine-readable
window metrics from the same isolated lifecycle run.
The machine-readable contract is documented in
`docs/validation/maintenance-window-evidence.md`.

The clean-install path must accept a selected parent and append exactly one
`FoxMouse` leaf. The release lifecycle gate must install to a non-default
isolated parent, then prove that Setup rediscovery, cached repair, detached root
uninstall, quiet Installed Apps uninstall and delete-settings uninstall all use
that same canonical root. Installed Apps metadata, shortcut target and working
directory must agree; the default root must stay unused, and a sibling sentinel
under the selected parent must survive every uninstall.

The current automated engineering report is
`docs/validation/v0.4.2-execution-report.md`. Its final v0.4.2 M5 run is PASS at
`artifacts/validation/m5-20260905-021308`, including current-host dark/150%
window captures and machine-readable layout evidence. Authenticode and the
remaining physical display, theme, DPI and Windows-version matrix stay explicit
external release gates; the engineering PASS does not certify those NOT RUN
combinations. The completed v0.4.1 and v0.4.0 reports remain immutable at
`docs/validation/v0.4.1-execution-report.md` and
`docs/validation/v0.4-execution-report.md`; neither validates v0.4.2 binaries.

The historical frozen v0.4.1 run completed its automated engineering gates at
2026-09-04 23:50:37 Asia/Shanghai; its evidence remains in
`artifacts/validation/m5-20260904-234737`. The v0.4.0 run remains in
`artifacts/validation/m5-20260904-180927`. Those directories are retained as
historical evidence and are not rewritten by v0.4.2 packaging.

## UI/settings delta gate

The WinUI/tray refresh is accepted only when the following evidence is attached
to a new M5 run; the historical v0.1.0 results below do not implicitly validate
new binaries:

| Area | Required evidence |
|---|---|
| Build | Release build of `FoxMouse.App`, `FoxMouse.Settings`, and all tests with no warnings/errors |
| Settings logic | `FoxMouse.Settings.Tests`, process-catalog tests, and settings-change IPC tests pass |
| Settings smoke | Packaged `Settings\FoxMouse.Settings.exe --smoke-test` exits 0 without reading/writing the user's settings; the real-window readiness smoke also proves XAML initialization |
| Runtime bridge | Save updates a running tray host without restart; preview succeeds with a host and reports unavailable without one |
| Fallback | Missing, crashing, or non-ready settings host opens WinForms settings/About and leaves the engine responsive |
| Tray popup | Build-tree and portable `FoxMouse.exe --tray-menu-smoke` checks pass; interactive review covers theme, keyboard, scaling, rounded corners, and taskbar-edge placement |
| Overlay z-order | Each presented frame restores `HWND_TOPMOST` without activation; ordinary/topmost apps do not leave the enlarged cursor covered |
| Visual/accessibility | Light, Dark, High Contrast, transparency-off, keyboard/focus, 1366x768, 100%-300% DPI, and 100%-200% text scale checklist |
| Process picker | Name/EXE/window-title search, visible-first order, background toggle, Refresh, Browse EXE, deduplication, and protected-process tolerance |
| Packaging | ZIP/Setup contain the settings subtree and root maintenance EXE; isolated custom-parent install, Setup rediscovery, same-version repair, legacy upgrade, uninstall, ARP, sibling preservation, no-scroll window metrics, Guard recovery, rollback and hash checks pass |

Useful reproducible commands are:

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' build .\FoxMouse.slnx -c Release
& 'C:\Program Files\dotnet\dotnet.exe' test .\FoxMouse.slnx -c Release
pwsh -NoProfile -File .\scripts\Test-DeploymentSafety.ps1
pwsh -NoProfile -File .\scripts\Accept-Milestone.ps1 `
  -Milestone M5 -Configuration Release -ExpectedVersion 0.4.2
```

Use `-AllowRealCursorHide` only on an interactive Windows desktop when the
release operator explicitly intends to run the bracketed real hide/show gate.

## v0.1.0 historical execution status

| Gate | Result | Evidence |
|---|---|---|
| M0 | PASS | Product contract, architecture, support matrix and web baseline are checked in |
| M1 | PASS | Release build, Guard fake smoke and bracketed real hide/show smoke |
| M2 | PASS | Core/Windows tests and 2/2 deterministic trace replay |
| M3 | PASS | Deployment safety, modal lifecycle smoke and packaged settings/tray application |
| M4 | PASS on current host | 600-frame performance/resource soak, 150% DPI environment capture and display checklist |
| M5 | PASS | `artifacts/validation/m5-20260904-105101`, including the WinUI settings subtree and bracketed real hide/show |

The current automated gate completed 201/201 tests with no skips: 66 Core,
118 Windows-platform, and 17 Settings tests. The release verifier also ran the
portable App, lifecycle, and Settings smoke tests and validated the Setup
payload. The older `m5-20260904-033002` directory remains historical evidence
for the earlier live install/upgrade/uninstall exercise.

Physical multi-display/theme-matrix certification and a numerical current-
macOS parity claim remain explicit external hardware gates, as described in
the execution report.
