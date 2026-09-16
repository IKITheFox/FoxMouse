# Display and pointer validation matrix

This checklist separates deterministic coverage from observations that require
physical displays. It is intentionally not a claim that one workstation can
certify every topology.

## Automated coverage in every M4/M5 gate

- [x] Hotspot anchoring at positive and negative virtual-desktop coordinates.
- [x] Cursor geometry at representative 1–4-display coordinate extents.
- [x] DIP conversion at 100%, 125%, 150%, 200%, 250% and 300%.
- [x] Independent X/Y scaling when the 1,024-pixel surface cap is reached.
- [x] Absolute input, device changes, desktop warps, queue backlog and a
  mixed-DPI boundary reset calibration instead of contaminating detector gain.
- [x] A display-topology event is fatal to an active high-fidelity effect and
  requests native-cursor restoration before cached cursor data is invalidated.
- [x] The overlay is non-activating, click-through and absent from the taskbar.
- [x] A 600-frame current-cursor and locator soak records frame-submit latency
  and verifies GDI, USER and process handle counts return to their baseline.

The exact assertions live in the Core and Windows test projects; the M5 evidence
directory contains the full test and performance logs.

## Setup and Maintenance window gate

Every v0.4.2 M5 run captures the real install, installed-maintenance and
uninstall-confirmation states together with machine-readable window metrics.
For each captured state:

- [ ] horizontal and vertical scrollbars are not visible;
- [ ] every visible control is inside the client rectangle;
- [ ] action buttons remain on one row and inside the footer;
- [ ] visible controls have no disallowed intersection;
- [ ] reported state and screenshot dimensions agree;
- [ ] Chinese text, the custom path and focus indicators are neither clipped
  nor covered by stale owner-drawn pixels.

Run the state cycle `maintenance → uninstall confirmation → back → uninstall
confirmation` repeatedly when visually certifying a candidate. Automated bounds
and screenshots catch structural regressions; stale compositor pixels still
require reviewing the resulting image on the supported desktop configurations.

## Physical interactive checklist

Run each available row on a local Windows 11 x64 console. Start with the native
cursor visible, use the tray preview, then shake with both a mouse and touchpad
when present. During every row, verify that the hotspot remains under the same
pixel, clicks pass through the overlay, growth/release is smooth, and disabling
or exiting immediately restores the native cursor.

| Topology | DPI combinations | Refresh combinations | Status on this validation host |
|---|---|---|---|
| 1 display | 100%, 150%, 200%, 300% | 60, 120/144, 240 Hz | Current active desktop exercised by smoke; exact values are captured in `environment.json` |
| 2 displays, secondary right | equal and mixed DPI | equal and mixed Hz | Not physically available |
| 2 displays, secondary left/up | equal and mixed DPI; negative coordinates | equal and mixed Hz | Automated geometry only |
| 3 displays | 100%–300% mixed | 60–240 Hz mixed | Automated geometry only |
| 4 displays | 100%–300% mixed | 60–240 Hz mixed | Automated geometry only |

For every physically available topology:

- [ ] Move and shake across every monitor boundary in both directions.
- [ ] Change the primary display and repeat.
- [ ] Change one monitor's DPI while FoxMouse is idle and while previewing.
- [ ] Disconnect/reconnect a display during an active preview.
- [ ] Exercise the top-left, top-right, bottom-left and bottom-right edges.
- [ ] Enter borderless full-screen and Windows Magnifier; confirm immediate
  native-cursor fallback.
- [ ] Lock/unlock, suspend/resume and exit during growth and release.
- [ ] Record a 120 fps or faster external video if perceptual timing is being
  compared with a particular Mac.

## Evidence boundary

The current automated gate proves coordinate/DPI math and lifecycle safety over
the declared matrix. Physical multi-display composition, panel refresh behavior
and mouse/touchpad feel remain hardware observations and must be checked on each
release candidate intended for broad distribution.
