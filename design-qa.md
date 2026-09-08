# Design QA — privacy Live Activity and compact accessory row

## Evidence

- Source visual truth:
  - `C:\Users\tanuj.DESKTOP-BOL1R68.000\desktop\dyamic island\.codex-reference\privacy-detached.png`
  - `C:\Users\tanuj.DESKTOP-BOL1R68.000\desktop\dyamic island\.codex-reference\privacy-text-card-after-first-fix.png`
  - `C:\Users\tanuj.DESKTOP-BOL1R68.000\desktop\dyamic island\.codex-reference\privacy-dot-compact-confirmed.png`
  - `C:\Users\tanuj.DESKTOP-BOL1R68.000\desktop\dyamic island\.codex-reference\airpods-widgets-stacked-before-row-fix.png`
  - `C:\Users\tanuj.DESKTOP-BOL1R68.000\desktop\dyamic island\.codex-reference\expanded-floating-privacy-dot-before-live-activity.png`
  - `C:\Users\tanuj.DESKTOP-BOL1R68.000\desktop\dyamic island\.codex-reference\countdown-widget-clipped-before-dynamic-rail.png`
- Source pixels:
  - Detached privacy screenshot: 742 × 681
  - First-fix privacy screenshot: 516 × 169
  - Confirmed compact-dot screenshot: 364 × 165
  - Stacked accessory-row screenshot: 962 × 209
  - Expanded floating-dot screenshot: 1276 × 490
  - Clipped countdown screenshot: 905 × 265
- Latest implementation screenshot: unavailable after the current rebuild
- Implementation viewport: native transparent WPF window with a 1200-DIP canvas
- Density normalization: not possible until a post-fix native capture is available
- Target state: expanded Apple-style island with camera active

## Intended visual behavior

- Compact mode shows only the 8-DIP green/orange recording light inside its 18-DIP trailing orb.
- Expanded mode replaces the outside orb with a 26-DIP inline Live Activity beneath the top-right controls.
- The expanded activity contains the semantic sensor dot, the live sensor state, and a short `LIVE` marker.
- Sensor activation while expanded uses a 260 ms scale/slide and 180 ms fade.
- AirPods and live widgets remain distinct cards in one 64-DIP horizontal accessory lane.
- The live-widget rail sizes from visible cards up to 332 DIP, fitting weather and countdown together while preserving AirPods space.
- A third widget and beyond can be reached with horizontal mouse-wheel scrolling.

The layout follows Apple's guidance to preserve information between compact and expanded presentations, keep content snug, avoid notification-style layouts, and animate existing live information into its new position.

## Comparison history

### Pass 1 — detached privacy notification

- [P1] Privacy notification was detached in the middle of the desktop.
  - Fix: removed privacy from the generic banner.

### Pass 2 — attached text card

- [P1] The attached privacy disclosure still read as a large notification.
  - Evidence: `privacy-text-card-after-first-fix.png`.
  - Fix: removed the disclosure and reduced compact privacy to a trailing recording orb.

### Pass 3 — compact dot

- User evidence: `privacy-dot-compact-confirmed.png` confirms the compact dot-only treatment.
- [P1] The orb initially disappeared in expanded mode.
  - Fix: kept sensor activity visible while moving the orb to the expanded shell edge.

### Pass 4 — accessory-row density

- [P1] AirPods and weather occupied two stacked full-width rows.
  - Evidence: `airpods-widgets-stacked-before-row-fix.png`.
  - Fix: combined them into one row as separate cards and reduced height reservation to one lane.

### Pass 5 — expanded state confirmation

- User evidence: `expanded-floating-privacy-dot-before-live-activity.png`.
- AirPods and weather are visibly separate, aligned on one row, and not clipped.
- [P1] The privacy orb remained detached from the expanded content and did not read as a Live Activity.
  - Fix: made the outside orb compact-only and added a snug inline activity beneath the expanded controls.

### Pass 6 — current implementation

- Release build: succeeded with zero warnings and zero errors.
- Automated tests: 97 passed, 0 failed, 0 skipped.
- Root app: republished and relaunched successfully as process 16712.
- Post-fix visual evidence: pending a same-state expanded screenshot.

### Pass 7 — countdown overflow

- [P1] The fixed 230-DIP widget viewport showed weather but clipped the countdown card at the island edge.
  - Evidence: `countdown-widget-clipped-before-dynamic-rail.png`.
  - Fix: replaced the fixed width with a content-derived rail capped at 332 DIP, reduced weather to 180 DIP and countdown to 136 DIP, and added horizontal mouse-wheel navigation for additional widgets.
- Release build: succeeded with zero warnings and zero errors.
- Automated tests: 97 passed, 0 failed, 0 skipped.
- Root app: republished and relaunched successfully as process 20344.
- Post-fix visual evidence: pending a weather-plus-countdown screenshot.

### Pass 8 â€” disconnected-AirPods accessory lane

- [P1] When no AirPods card was present, weather and countdown remained confined to the 332-DIP rail, leaving the rest of the 64-DIP accessory lane empty.
  - Evidence: user-supplied expanded-island screenshot, `codex-clipboard-2QE2Zm.png`.
  - Fix: the widget rail now occupies the full accessory lane when AirPods are absent. Enabled cards divide that width evenly; when AirPods reconnect, cards return to their compact widths beside the dedicated AirPods card.
- Release build: succeeded with zero warnings and zero errors.
- Automated tests: 97 passed, 0 failed, 0 skipped.
- Root app: republished and relaunched successfully as process 16396.
- Post-fix visual evidence: pending a screenshot of the same disconnected-AirPods state.
## Required fidelity surfaces

- Fonts and typography: the inline activity uses the existing Segoe UI Variable hierarchy at 10.5 and 8 DIP with semibold/bold optical contrast; post-fix capture is pending.
- Spacing and layout rhythm: the activity fits the existing fixed 64-DIP header region and does not add shell height; its final alignment requires capture.
- Colors and visual tokens: the opaque dark activity surface, subtle keyline, Apple green camera state, and Apple orange microphone state follow existing tokens.
- Image quality and asset fidelity: no new imagery was required; the confirmed AirPods raster remains sharp and uniformly scaled.
- Copy and content: compact privacy is text-free; expanded privacy exposes only the sensor state and `LIVE`, without inventing an app identity.

## Findings

- [P1] The rebuilt inline Live Activity has not been captured.
  - Location: expanded Apple-style header beneath the top-right controls.
  - Evidence: the pre-fix screenshot shows the detached orb, while the new build and process evidence do not show the revised pixels.
  - Impact: final pill width, truncation, control clearance, and motion endpoint cannot yet be visually certified.
  - Fix: capture the expanded camera-active state and compare it directly with the pre-fix expanded screenshot.
- [P1] The rebuilt dynamic widget rail has not been captured, including the disconnected-AirPods fill state.
  - Location: expanded AirPods and live-widget accessory lane.
  - Evidence: the source screenshot shows countdown clipped after weather; code, build, test, and process evidence confirm the fix is running but do not show its pixels.
  - Impact: final AirPods truncation and full countdown visibility cannot yet be visually certified.
  - Fix: capture the expanded state with AirPods, weather, and countdown visible.

## Implementation checklist

- Capture the expanded camera-active state.
- Confirm the outside orb is absent.
- Confirm the inline activity is snug, legible, and clear of the controls and progress bar.
- Capture the disconnected-AirPods state with weather and countdown enabled; confirm both cards fill the accessory lane without overflow.
- Capture AirPods, weather, and countdown together and confirm all three cards remain legible.

final result: blocked

## Core upgrades verification — 2026-09-07

Scope: isolated `codex/island-core-upgrades` worktree, based on `ae5c8af`. The earlier production-capture findings above are retained as historical notes. This pass uses synthetic fixtures in real WPF controls; it does not certify live hardware behavior.

- Release build: zero warnings and errors. Unit tests: 119 passed. Native assertions: 40 passed. Binding diagnostics: empty.
- Evidence: `artifacts/upgrade-verification/5a841432d9a147fb866395aab184b64b/captures/` contains 42 local PNGs (14 surfaces at three raster scales) and `checks.txt`. These artifacts are excluded from Git.
- Apple with and without AirPods, Stats, larger text, widget overflow, timers and alarms at the top and bottom of their scroll range, grouped notifications, Q with a ringing alarm, and constrained Apple/timer/Q views were rendered.
- Two accessory cards fill the 708-DIP lane without false overflow. Additional widgets remain reachable by scrolling. The timer editor has a 304-DIP viewport for 658 DIPs of content; its stopwatch and lower controls are reachable. Alarm recurrence and snooze controls are reachable at the bottom.
- Q retains its draft and continues rendering response chunks while the attached urgent strip offers snooze and dismiss. The simulated 640-by-480-DIP working area bounds the shell and keeps the composer reachable through scrolling.
- Legacy timer migration retains an exact backup. Future-version storage is preserved. Notification grouping, dismissal, Focus Mode, history-disabled delivery, and permission placeholders passed native assertions.
- Raster scaling is not physical mixed-DPI testing. Real AirPods reconnects, Windows permission prompts, audible alerts, suspend/resume, and physical monitor transitions remain device-validation items.

Result: automated and fixture-based validation passed; live-device validation remains as described above. The original checkout and running application were not replaced.

## Expansion scrollbar and layout regression — 2026-09-07

The outer ScrollViewer introduced by the upgrade displayed automatic scrollbars while the shell animated through smaller sizes. It also resized the expanded subtree during the morph. The outer viewport now uses hidden scrollbar chrome and stays at the destination dimensions while the rounded shell reveals its content. Inner timer and transcript scrolling remain available.

Verification: 119 unit tests passed; Release build has zero warnings/errors. Native verification now exercises expressive animation, checks five in-flight samples for stable viewport size and absent outer scrollbars, limits expanded-content resize events, verifies interrupted morph recovery, and checks that the constrained viewport still scrolls. Evidence: `artifacts/upgrade-verification/47b8cf28002641cdb1c88acd2573bb6e/captures/`. This confirms removal of repeated content resizing; it is not a live GPU frame-rate measurement.

The follow-up morph pass moves the shell with a render transform and commits layout dimensions only at the start and end. The same native checks passed after this change, with no per-frame Width/Height animation remaining.
