# FoxMouse UI contract

This contract covers the tray surface, the settings window, the excluded-app
picker, About, and user-facing error messages. It deliberately excludes the
cursor overlay, Raw Input, and Guard safety protocol.

## Platform strategy

- `FoxMouse.App` remains the WinForms/Win32 tray and cursor-engine host.
- The tray uses native WinForms controls with the .NET 10 system color mode.
- `FoxMouse.Settings` is the preferred on-demand, out-of-process WinUI 3
  settings surface. It is unpackaged, self-contained for the Windows App SDK,
  and single-instance per Windows user. A second activation brings the existing
  window forward and selects the requested General, Exclusions, or About page.
- The tray host searches for `Settings\FoxMouse.Settings.exe` beside
  `FoxMouse.exe`, then for a same-directory development layout. The WinForms
  settings/About dialogs remain a safe fallback when neither candidate can be
  found or the process cannot be started.
- No UI operation may terminate, inject into, elevate for, or otherwise modify
  a process selected for exclusion.

## Implemented window structure

- WinUI uses an extended native title bar, `NavigationView`, scrollable page
  content, cards, `ToggleSwitch`, `Slider`, `NumberBox`, `AutoSuggestBox`,
  `InfoBar`, and a persistent Save/Revert footer.
- Mica is enabled only when the platform reports support and High Contrast is
  off. A system-theme solid background remains underneath and becomes visible
  when Mica is unavailable. WinUI theme resources track system light/dark,
  accent, and control colors without restarting the cursor host.
- The WinForms fallback applies DWM dark-title-bar attributes and semantic
  `SystemColors`; High Contrast switches its menu renderer back to Windows'
  system renderer.
- The application artwork is independent FoxMouse artwork. Brand orange is
  confined to that artwork; controls continue to use the user's accent color.
- The supplied FoxMouse mark has Light, Dark, High Contrast Black, and High
  Contrast White variants. The active variant follows Windows at runtime;
  static Shell surfaces use a multi-resolution neutral application ICO.

## Visual tokens

Use semantic system resources instead of literal light or dark colors:

| Role | WinForms fallback | WinUI 3 |
| --- | --- | --- |
| Window | `SystemColors.Window` | `ApplicationPageBackgroundThemeBrush` |
| Surface | `SystemColors.Control` | `CardBackgroundFillColorDefaultBrush` |
| Primary text | `SystemColors.WindowText` | `TextFillColorPrimaryBrush` |
| Secondary text | `SystemColors.GrayText` | `TextFillColorSecondaryBrush` |
| Border | `SystemColors.ControlDark` | `CardStrokeColorDefaultBrush` |
| Accent/action | system highlight/accent | `AccentFillColorDefaultBrush` |
| Focus | system focus cues | standard WinUI focus visual |

High Contrast always takes precedence over branding and backdrop materials.
Brand orange is limited to the FoxMouse artwork; it is not a replacement for
the user's system accent color.

- Font: Segoe UI Variable where available, Segoe UI fallback.
- Spacing scale: 4, 8, 12, 16, 24, and 32 device-independent pixels.
- Standard interactive height: at least 32 device-independent pixels.
- Icons: 16 or 20 pixels in commands; multi-resolution application icon.
- Layout: content may scroll, but Save and Cancel remain visible.

## Tray information architecture

1. Enable (checked state is the status indicator).
2. Preview enlarged cursor.
3. Open settings.
4. Separator.
5. Open diagnostics.
6. About FoxMouse.
7. Separator.
8. Exit.

The tray tooltip contains the detailed engine status. The popup does not use a
disabled status row or duplicate the product name in every command.

## Settings information architecture

1. Master enable switch.
2. Cursor enlargement: mode, shake sensitivity, maximum scale, preview.
3. Pause rules: dragging and full-screen applications.
4. Excluded applications: searchable picker and removable rows.
5. System: start with Windows.

The visible mode names are “原生” and “定位环”. Their serialized enum values are
unchanged for backward compatibility. The implementation must not claim that
the native mode automatically falls back to a locator ring; “定位环” is an
explicit user choice.

Cards show only the setting name and control. Explanatory text appears as a
pointer tooltip and is also exposed as accessibility HelpText. The sensitivity
slider uses a separate responsive row with endpoint padding. Maximum scale is
localized with at most two decimals and no insignificant trailing zeros.

The About surface contains the FoxMouse mark, product name, and version only.
Save confirmation is informational, has no close button, and automatically
dismisses after 10 seconds; a newer status cancels the previous timer.

## Excluded-app picker

- Search the current in-memory snapshot by app name, executable name, or
  visible-window title.
- Show interactive applications first and expose background processes only on
  request.
- Deduplicate by executable basename using ordinal, case-insensitive matching.
- Store only a normalized executable basename such as `game.exe`; never store
  a PID.
- Provide Refresh and Browse for an executable that is not currently running.
- Access denial, process exit, missing metadata, and missing icons are normal
  per-row conditions and cannot fail the picker.
- Exclude PID 0, FoxMouse, FoxMouse.Guard, and FoxMouse.Settings from candidates.

Process discovery is asynchronous and fault-tolerant. It inspects only the
current Windows session, groups duplicate executable basenames
case-insensitively, and treats access denial, process exit, a missing icon, or
unreadable metadata as a row-level omission/fallback rather than a picker
failure. The WinForms picker displays an executable icon when one can be read;
the WinUI list has a deterministic monogram fallback.

## Persistence, hot reload, and preview

- Both settings surfaces read and write the existing settings schema at
  `%LOCALAPPDATA%\FoxMouse\settings.json`; this UI iteration does not add a
  schema version or migrate user data.
- Save normalizes values and executable basenames, performs the existing atomic
  file replacement, and only then sends a `reload-settings` message to the tray
  host. A failed notification is reported as "saved for next start" and never
  rolls back the file.
- Preview sends a separate `preview` command to the running engine. If the form
  is dirty, the settings host first performs the same atomic save and reload
  notification as Save, then sends Preview. This keeps the IPC surface small
  while ensuring the preview reflects the values currently shown.
- The settings-change channel is a bounded newline-delimited JSON message over
  a `CurrentUserOnly` named pipe. Messages carry a protocol version, command,
  absolute settings path, unique revision, and UTC timestamp. The tray accepts
  only known commands for its exact normalized settings path, then dispatches
  work onto its UI context.
- Reload and preview requests are serialized with tray user operations. Reload
  applies engine settings, startup registration, menu state, and status without
  recreating the Raw Input sink, overlay, or Guard connection.

## Accessibility and adaptation

- Support Windows Light, Dark, High Contrast, transparency-off, and system
  accent changes without requiring an application restart.
- Support 100%, 125%, 150%, 200%, and 300% DPI and 100%-200% text scaling.
- Preserve a logical keyboard order, visible focus, Enter/Save, and Esc/Cancel.
- Every non-text control has an accessible name; helper text is associated with
  its setting; icon-only remove buttons expose the target application name.
- At 1366x768, no command is clipped and the settings content can be reached by
  keyboard and scrolling.

## Release gates

- Existing settings JSON loads unchanged and round-trips without a schema bump.
- A theme change cannot restart or recreate the cursor engine, Raw Input sink,
  overlay, or Guard connection.
- The WinUI settings process is single-instance per user and notifies the tray
  host after an atomic save. Failure to notify does not undo a successful save.
- Release artifacts include the settings host and retain the existing cursor
  recovery, lifecycle, installation, upgrade, and uninstall gates.
- Automated checks cover activation parsing, executable-name normalization,
  current-session process filtering, settings-change message validation, and
  non-visual settings smoke. Before release, manually inspect Light, Dark, High
  Contrast, transparency-off, 100%-300% DPI, 100%-200% text scale, keyboard
  traversal, and the 1366x768 minimum layout; a build alone is not visual proof.
