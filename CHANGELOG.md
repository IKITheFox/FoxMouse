# Changelog

## 1.0.0 Beta

- Unified online and offline installers on the shared spacious, theme-aware maintenance UI.
- Display `1.0.0 Beta` in installer and About surfaces; use `1.0.0-beta` product metadata and numeric `1.0.0.0` file versions.
- All successful maintenance operations wait for confirmation before closing.
- Online setup selects the destination before downloading missing dependencies; offline setup retains self-contained runtimes.
- This is a beta, not a declaration of completed clean-Windows or multi-monitor acceptance.

## 0.5.0 — Unreleased candidate

- Expanded the scale input and split application blocking into two searchable lists.
- Removed the locator ring's white outline and added bounded stationary cursor recovery.
- Coordinated tray exit with unsaved settings decisions and kept installation completion visible until confirmation.
- Added Chinese/English UI resources, installer language selection, and preservation of installed language preferences.
- Added a framework-dependent online installer with pinned Microsoft dependency downloads, integrity checks and cancellation.
- Final visual, clean-runtime and interaction acceptance remains in progress; this entry is not a release announcement.

## 0.4.2 — Single-page Maintenance and Custom Location

- Reworked Setup and Maintenance as mutually exclusive single-page states so
  install, maintenance, uninstall confirmation, running and failure content fit
  inside one window without horizontal or vertical scrolling.
- Corrected owner-drawn control invalidation, DPI scaling and Chinese font
  measurement to prevent stale button pixels, overlapping labels and clipped
  actions after state, focus, theme or monitor changes.
- Added a native folder picker for choosing an installation parent. FoxMouse
  appends exactly one `FoxMouse` leaf, validates the destination before any
  mutation, and keeps the chosen location through repair, upgrade and uninstall.
- Extended the isolated installer lifecycle gate to exercise a custom parent,
  Installed Apps metadata, cached repair, detached uninstall, setting retention,
  sibling preservation and absence of writes to the default installation root.
- Added state-specific maintenance screenshots and machine-readable window
  metrics as release evidence. Any visible scrollbar, clipped control or
  overlapping visible control blocks the v0.4.2 release.

## 0.4.1 — Maintenance UI and Exit Reliability

- Rebuilt the graphical Setup and Maintenance layout around DPI-aware,
  content-sized rows so Chinese copy, the installation path, progress state,
  and footer actions remain aligned across supported display and text scales.
- Replaced the mixed classic/dark maintenance controls with a lightweight
  Windows-themed presentation layer, including coherent light, dark,
  high-contrast, focus, disabled, progress, and destructive-action states.
- Removed the blocking success message box. Successful install, upgrade,
  repair, and uninstall operations now finish their transaction and close the
  maintenance process automatically; failures remain visible and retryable.
- Unified the Close button, title-bar close command, Escape, and Alt+F4 under
  the same lifecycle policy. Closing is immediate while idle or failed and is
  safely deferred while a deployment transaction is running.
- Added release-version consistency checks and lifecycle timing evidence to
  prevent a package, manifest, Installed Apps entry, or maintenance binary
  from silently shipping with mismatched version metadata.

## 0.4.0 — Native Maintenance and Brand Refresh

- Replaced the script-driven setup path with graphical, self-contained Setup
  and Maintenance applications. Re-running Setup now offers repair and
  uninstall, while Installed Apps and the installation-root maintenance EXE
  use the same transactional engine.
- Added per-user Installed Apps registration, a verified local repair cache,
  Start menu integration, legacy-install migration, safe process shutdown, and
  rollback for interrupted install, repair, upgrade, and uninstall operations.
- Adopted the selected FoxMouse logo kit across executables, tray UI, settings,
  About, Setup, and Maintenance, with light, dark, and high-contrast variants
  selected from the active Windows theme.
- Simplified the General and About pages: concise “原生 / 定位环” mode names,
  hover-only help, a responsive sensitivity slider, trimmed localized scale
  values, and a neutral non-closable save notice that dismisses after 10 seconds.
- Fixed the stationary-pointer repaint race after Preview. Once native cursor
  visibility is acknowledged, FoxMouse now removes the replacement overlay and
  explicitly refreshes the cursor-owning window without moving the pointer.
- Preserved serialized mode identifiers for backward compatibility while
  removing obsolete explanatory and affiliation copy from the product UI.

## 0.3.0 — Resilient Bloom and 8K Rendering

- Replaced permanent runtime lockout with a classified recovery circuit:
  transient overlay, cursor, device, heartbeat and Guard failures now cool down,
  probe half-open, and recover automatically with bounded exponential backoff.
- Moved Guard lease renewal to a background supervisor, added automatic Guard
  replacement, and tied renewal to a fresh successful render heartbeat. A
  stalled UI therefore restores the native cursor instead of renewing forever.
- Added a bounded hide-acknowledgement hand-off and rejects stale hide success,
  preserving the strict restore-native-before-removing-overlay invariant.
- Fixed input/render clock skew, added multi-device activity aggregation and
  buffered Raw Input draining for high-polling-rate mice.
- Replaced button-down suppression with a real drag classifier based on the
  Windows drag rectangle; clicks and double-clicks no longer cancel the effect.
- Replaced first-order scaling with a frame-rate-independent damped spring,
  including a subtle elastic return and a 300 ms minimum accepted visibility.
- Added cursor resource resolution up to a 2048-pixel local surface, per-monitor
  DPI resolution, a reusable 32-bit DIB renderer, and explicit raster quality
  reporting for 8K and mixed-DPI environments.
- Expanded fault, timing, 8 kHz, 8K geometry, high-resolution performance and
  GDI/USER/handle leak tests.

## 0.2.0 — Windows 11 Interaction Refresh

- Replaced the legacy tray context menu with a rounded Windows 11-style popup
  that follows the system light, dark, accent-color, and high-contrast state.
- Made Settings and About launch observable: FoxMouse now requires the external
  WinUI host to become ready and uses the in-process fallback if startup fails,
  exits early, or times out.
- Reasserted the cursor overlay's topmost z-order after frame presentation so
  ordinary and topmost application windows cannot permanently cover the
  enlarged pointer.
- Added build-tree and portable-package tray-menu smoke gates to catch popup
  construction and rendering regressions before release.
- Secure desktop surfaces—including UAC consent, sign-in, and the lock screen—
  remain intentionally outside FoxMouse's overlay boundary.

## 0.1.0 — Public Preview

- Added device-isolated Raw Input shake detection with deterministic replay.
- Added smooth score-driven cursor scale animation.
- Added current-cursor capture and hotspot-anchored layered rendering.
- Added independent visibility Guard with expiring hide lease and fallback.
- Added safe locator-ring compatibility mode.
- Added mixed-DPI input normalization, full-screen/remote/drag policies and
  per-process exclusions.
- Added tray controls, settings, login startup, bounded local logs and explicit
  trace recording.
- Added a single-instance WinUI 3 settings host using system theme resources,
  standard Windows controls, Mica where available, and a solid/high-contrast
  fallback. The in-process WinForms settings and About windows remain available
  when the external host cannot be launched.
- Redesigned the tray menu around native system colors, concise checked-state
  status, themed command icons, and a multi-resolution FoxMouse application icon.
- Replaced manual process-list editing with a searchable excluded-application
  picker. It searches app name, executable name, and visible-window title;
  prioritizes visible apps; can reveal background processes; and supports
  Refresh and Browse EXE without persisting PID, title, or full path.
- Added same-user local IPC for save-triggered settings hot reload and preview
  requests. Saving remains successful if no tray host is available.
- Added portable and per-user setup packaging plus M0–M5 validation gates.
