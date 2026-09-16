# macOS network behavior baseline

Status: **semantic baseline complete; numerical visual baseline provisional**.

This document satisfies the M0 web-research requirement. Apple documents the
interaction but does not publish its gesture thresholds, peak scale, animation
duration or easing. Public demonstrations are therefore parameter seeds, not a
substitute for a controlled current-Mac measurement.

## Officially established behavior

- Apple describes a setting that temporarily makes the pointer larger when the
  user quickly moves the mouse or a finger on the trackpad. The current guide
  covers macOS Tahoe 26: [Make the pointer easier to see on Mac](https://support.apple.com/en-hk/guide/mac-help/mchlp2920/mac).
- Apple's El Capitan launch material explicitly described moving back and forth
  on the trackpad or shaking the mouse, supporting an oscillation detector
  rather than a one-way speed threshold: [OS X El Capitan — What's New](https://www.apple.com/ie/osx/whats-new/).
- Pointer shape, permanent size, outline and fill are separate accessibility
  concepts: [Pointers in macOS](https://support.apple.com/en-gb/guide/mac-help/mh35695/26/mac/26).
- AppKit exposes `disableCursorLocationAssistance`, establishing that an app may
  suppress the effect: [Apple Developer documentation](https://developer.apple.com/documentation/appkit/nsapplication/presentationoptions-swift.struct/disablecursorlocationassistance).
- Tracking speed and pointer acceleration are separate settings and are not in
  FoxMouse v0.1 scope: [Apple tracking settings](https://support.apple.com/en-ie/guide/mac-help/-mchlp1138/mac).

## Public real-machine observations

These observations are not Apple specifications:

- Igor Kromin's El Capitan real-machine recording shows rapid side-to-side
  motion and a temporary enlarged pointer. The author reports that QuickTime
  did not capture the enlarged cursor and used an external recording, which is
  why ordinary screen recordings cannot be treated as exact evidence:
  [article and recording](https://www.igorkromin.net/index.php/2015/10/03/and-the-best-osx-1011-el-capitan-feature-is-shake-to-locate/),
  [YouTube video](https://www.youtube.com/watch?v=p2rdXQgoago).
- A Monterey walkthrough describes rapid back-and-forth movement, temporary
  growth and a pointer hotspot at the tip:
  [MacMost demonstration](https://macmost.com/changing-the-pointer-size-and-color-on-a-mac.html).
- A 2016 real-use report describes roughly one second to reach the fully
  enlarged state: [The Mac Observer](https://www.macobserver.com/macos/optimize-cursor-size-shakability/).
- Later demonstrations confirm that stopping returns the pointer to normal and
  that it remains usable while enlarged:
  [AppleInsider](https://appleinsider.com/articles/21/06/29/how-to-find-your-lost-cursor-by-making-it-bigger-in-macos),
  [MacRumors](https://www.macrumors.com/how-to/make-mac-cursor-mouse-pointer-bigger/).

The old El Capitan footage appears to switch an I-beam to an enlarged standard
arrow during location assistance. That version-specific observation must not be
assumed for macOS 26 without a current-machine capture.

## v0.1 engineering seeds

| Parameter | FoxMouse seed | Evidence status |
|---|---:|---|
| Detector window | 420 ms | product-tuned provisional value |
| Minimum valid reversals | 3 | consistent with repeated back-and-forth semantics |
| Peak scale | 3.5x | within the publicly observed 3–4x visual range |
| Growth time constant | 75 ms after trigger | provisional; continuous and no overshoot |
| Stop hold | 180 ms | product debounce, not claimed as Apple behavior |
| Release time constant | 280 ms | about 645 ms to remove 90% of residual scale |
| Drag behavior | suppress by default | FoxMouse safety policy, not an Apple finding |

The detector uses path, speed, PCA dominant axis, reversal strokes and net/path
ratio. A high-speed straight movement is a negative sample. X, Y and diagonal
oscillation are accepted.

## Remaining exact-parity gate

A numerical "same as macOS" claim requires a current macOS 26.x machine, fixed
display/input settings, deterministic USB HID replay and an external 120/240fps
camera. Required test dimensions are direction, 30–180 logical-pixel amplitude,
2–8Hz frequency, 0.25–1.5s duration, straight/circle/jitter negatives, buttons,
screen edges and display crossings.

Per trace, measure trigger decision, first growth, t50/t90, peak scale,
stop-to-release delay, 90% recovery, shape and hotspot drift. Target gates are
98% stable-sample agreement, curve MAE at most 5%, peak error at most 5%, t50/t90
within 50ms and hotspot drift at most one logical pixel.
