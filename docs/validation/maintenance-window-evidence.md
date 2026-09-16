# Maintenance window evidence contract

FoxMouse v0.4.2 records one PNG and one JSON document for each required Setup
or Maintenance state. The PNG must contain the complete framed top-level window,
including its non-client title bar. Layout assertions use client coordinates.

## JSON schema

The current schema identifier is `foxmouse.maintenance-window/1`:

```json
{
  "schema": "foxmouse.maintenance-window/1",
  "scenario": "uninstall-confirmation",
  "uiState": "ConfirmingUninstall",
  "dpi": 144,
  "clientBounds": { "x": 0, "y": 0, "width": 680, "height": 480 },
  "captureBounds": { "x": 0, "y": 0, "width": 696, "height": 519 },
  "clientCaptureBounds": { "x": 8, "y": 31, "width": 680, "height": 480 },
  "autoScroll": false,
  "horizontalScrollVisible": false,
  "verticalScrollVisible": false,
  "contentHorizontalScrollVisible": false,
  "contentVerticalScrollVisible": false,
  "rootDock": "Fill",
  "rootBounds": { "x": 0, "y": 0, "width": 680, "height": 480 },
  "visibleControlsInsideClient": true,
  "overlapCount": 0,
  "overlaps": [],
  "clippedControls": [],
  "footerSingleRow": true,
  "visibleFooterButtons": ["ConfirmUninstall", "CancelUninstall"],
  "visibleControls": [
    { "name": "KeepSettings", "parent": "FeedbackLayout", "x": 68, "y": 300, "width": 310, "height": 28 }
  ]
}
```

`captureBounds.width` and `captureBounds.height` must exactly match the PNG
dimensions and must include more vertical pixels than `clientBounds`, proving
that the title bar was captured. `clientCaptureBounds` records the exact client
rectangle in PNG coordinates; it must remain completely inside `captureBounds`
and have the same dimensions as `clientBounds`. `rootBounds` must exactly fill
`clientBounds`. All four scroll flags, `autoScroll`, `overlapCount` and both
problem arrays are hard gates.

`overlaps` reports only invalid sibling intersections after converting bounds
to client coordinates. Parent/child containment, a label intentionally inside
a card and mutually hidden state controls are not overlaps. Invisible controls
must not appear in `visibleControls` or `visibleFooterButtons`.

## Required states

| Scenario | UI state | Required footer buttons |
|---|---|---|
| `idle` in clean Setup | `Idle` | `PrimaryAction`, `CloseAction` |
| `repair` before the operation starts | `Idle` | `RepairAction`, `UninstallAction`, `CloseAction` |
| `uninstall-confirmation` | `ConfirmingUninstall` | `ConfirmUninstall`, `CancelUninstall` |

The confirmation scenario calls the non-destructive test hook, records evidence,
then requests close. It must never begin the uninstall transaction. The
lifecycle script subsequently verifies that every installed file, registry
entry and shortcut is still present.

The JSON gate detects structural layout regressions. The PNG remains necessary
because stale compositor or owner-drawn pixels can be present even when all
control bounds are valid. The lifecycle gate samples only
`clientCaptureBounds`, quantizes colors to tolerate font smoothing and GPU
differences, and requires meaningful luminance/color variation in at least two
of twelve client-area tiles. It therefore rejects a blank client even if the
title bar and frame render correctly, while two-color high-contrast themes
remain valid. Each lifecycle run also creates an in-memory-equivalent negative
control by preserving the framed screenshot and replacing only the client area
with solid white. The gate must reject that image and removes it immediately;
accepting it is a release failure.
