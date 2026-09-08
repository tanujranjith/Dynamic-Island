# Core upgrades — isolated implementation

Branch: `codex/island-core-upgrades`.

Starting release: `5174a86`. Baseline including the five original uncommitted privacy/island edits: **`ae5c8af`**. The original checkout and installed executable are not replaced by this work.

## Try the isolated preview

Run `scripts/Start-UpgradePreview.ps1`. It launches the published build in `artifacts/core-upgrades` with separate settings, timers, history, logs, secrets, and instance identity. Startup registration, global hotkeys, Codex access, and alert sounds are disabled for previews. The initial preview position is lower than the production island.

Use this launcher instead of double-clicking the raw executable when comparing builds. A normal launch intentionally retains the existing production data location.

## What changed

- Scheduled alarms no longer replace music or Q. The activity menu can pin media or a selected timer. Ringing alarms and newly completed timers temporarily take priority; an attached alert preserves Q and its draft.
- Multiple named timers and saved alarms have independent controls. Start adds a timer; Restart reuses the selection. Custom duration presets and the stopwatch are available in the timer panel. The compact orb chooses a pinned active timer or the next running timer and shows the active count.
- `Ctrl+Alt+T` pauses/resumes the displayed timer or starts a ten-minute timer when none is active. Open the editor through the timer button or orb. Preview hotkeys are disabled to avoid competing with the original app.
- Notifications are compared by source identity, ID, and creation time, then queued and grouped by app per poll. History retains up to 100 individual items for seven days. Banners wait during Q/editing, with old queued groups summarized after 30 seconds; pending groups are capped at 50. History-disabled notifications remain in memory only.
- Calendar, notification, and clipboard settings display connection/permission/error states and recovery actions. Empty results are distinct from failed integrations. Unavailable Windows APIs remain unavailable; this change does not add packaging or new calendar providers.
- Accessory widths use the measured lane. Overflow has both navigation directions and keyboard support. Expanded content is bounded by monitor working area; timer editors scroll within an explicit viewport.

## Persistence and recovery

Timer storage version 2 migrates the single timer/alarm to ID-addressed collections and retains `timer-alarm.json.pre-v2.bak`. Unknown future versions and unreadable data are preserved; a visible warning explains when changes cannot persist. Writes are atomic.

Timer countdowns use a monotonic clock while running and rebase their persisted state before saving. Missed timers/alarms after restart or sleep produce a quiet summary; repeating alarms advance. Completed timers chime once and remain in the list. Alarms retain one-minute sound timeout and five-/ten-minute snooze. Spring DST gaps use the first valid minute; repeated fall times use the first occurrence.

## Verification and limitations

Validated on 2026-09-07: 119 unit tests passed; Release build completed with zero warnings and errors; 40 native WPF assertions passed with 42 fixture captures and an empty binding-diagnostics log. The preview executable is published locally in `artifacts/core-upgrades`. Captures and preview data are excluded from Git.

Run `scripts/Verify-CoreUpgrades.ps1 -Dotnet <path-to-dotnet.exe>`. The native harness uses fixture media, weather, privacy, notifications, timers, and Q; it does not start live capture, media, Bluetooth, or provider services. It renders real WPF controls, exercises the controls, and records assertions/captures under `artifacts/upgrade-verification`.

Raster captures at 100%, 150%, and 200% and a simulated 640×480-DIP working area are included in verification. These are not proof of physical mixed-DPI monitor transitions. Real AirPods reconnects, calendar/notification permission flows on another Windows configuration, audible output, and physical suspend/resume still require device testing. Windows notifications that disappear between polls cannot be recovered.

## Rollback

The original application remains the immediate fallback: quit the preview and continue using it. No merge or installation replacement is performed.

To inspect the exact starting source without disturbing either checkout, create another worktree from `ae5c8af`. Feature commits are layered; revert dependent UI commits before reverting their underlying models/services. Do not reset or clean the original dirty `main` checkout. Do not open version-2 timer data with the older application; use the retained pre-migration backup if a later production rollout is reversed.
