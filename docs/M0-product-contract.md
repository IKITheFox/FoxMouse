# M0 product contract

## Outcome

FoxMouse v0.1 is a per-user Windows 11 x64 tray utility. Rapid back-and-forth
pointer motion enlarges the current Windows cursor around its true hotspot. The
visual follows the pointer without modifying pointer coordinates or input
delivery and shrinks after motion stops.

## Tier 1

- Supported Windows 11 x64 releases.
- Normal interactive desktop and standard system cursors.
- 100%–300% per-monitor DPI, negative virtual coordinates and 60–240 Hz.
- Mouse and touchpad motion that reaches the standard pointer pipeline.

## Safe fallback

In high-fidelity mode, FoxMouse leaves the native cursor untouched and draws
nothing when the cursor cannot be represented safely. The locator ring is used
only when the user explicitly selects compatibility mode. Unsupported states
include secure desktop, hidden/software/unsupported animated cursors, remote
sessions, exclusive full-screen applications and visibility-controller failure.

## Non-goals

- Pointer acceleration or movement smoothing.
- Input injection, hooks, drivers or permanent cursor-scheme changes.
- Apple cursor assets or claims of Apple affiliation.
- Account, cloud sync, auto-update, ARM64/x86 or Store submission in v0.1.

## Release invariants

1. FoxMouse never changes the pointer position or input sequence.
2. The native cursor is hidden only after a replacement frame is ready and the
   guard owns a live recovery lease.
3. Crash, hang, shutdown, lock, suspend and renderer failure restore the native
   cursor before teardown.
4. Unsupported states favor a visible native cursor over visual fidelity.
