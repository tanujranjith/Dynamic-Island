# Q Compare design verification

final result: passed

## Visual targets

- Expanded: `C:/Users/tanuj/.codex/generated_images/01a0938f-4117-7060-a2b0-01e27cab1b0a/exec-9a7befd8-7146-4c52-a881-c90f567c14cc.png`
- Selected collapsed: `C:/Users/tanuj/.codex/generated_images/01a0938f-4117-7060-a2b0-01e27cab1b0a/exec-92b57a49-2544-4c6f-b688-c3bbce45904e.png`
- Final native implementation: `artifacts/compare-verification-v5/captures/compare-expanded-100.png`, `compare-collapsed-detail.png`, `compare-narrow-100.png`, and `compare-narrow-scrolled-100.png`.

## First inspection

- P1: Second provider/model dropdowns went blank when streaming replaced their item sources. Change item-source notifications to run only on configuration changes; add a selected-value regression assertion.
- P2: Compact clock remained visible next to the two answers. Hide ordinary status in compact comparison mode.
- P2: Compact presentation lacked the selected mockup's slate outline and blue Q tile. Add comparison-only styling without changing the normal island size settings.
- P2: Narrow header could overlap the Compare toggle. Trim title text and move nonessential timer/mute controls out of compare mode.
- Capture-only issue: Directly rendering the transformed shell produced an offset crop. Crop from the full native-window render instead.

## Pending validation

Rebuild, rerun native assertions, and compare revised expanded and collapsed captures alongside their selected visual targets. Check typography, spacing, colors, existing native icon fidelity, and provider-labeled content. Verify both cards remain reachable by scrolling on a 640 × 480 work area.

## Second inspection

Compared both source mockups and v2 native captures in the same tool input. The compact outline, Q tile, two answer lines, and clock suppression now match the intended structure. The second selectors had valid selected values but their template still rendered blank labels. Replace the cached SelectionBoxItem presenter with a direct SelectedItem text binding. Narrow header status also needs clipping within its own column. Keep blocked pending the final render check.

## Final inspection and evidence

The v3 model label rendered, but the second provider remained blank. Stable filtered provider choices and a two-way SelectedItem binding resolved it in v4. The final v5 fixture also changes the second provider through the actual control, confirms the rendered template label, and passes. No deferred UI synchronization workaround was retained.

Compared the expanded reference and final v5 expanded capture in the same image-tool input. Compared the collapsed reference and v3 focused capture together, then inspected the unchanged compact presentation in the final v5 crop. Inspected final narrow and narrow-scrolled captures. Earlier P1/P2 findings are resolved: both selectors display their selected names, both answers are distinct, no compact clock competes with the answers, the compact outline/blue Q tile are present, and narrow header content cannot overlap its controls.

### Dimensions and state

- Expanded reference: 1550 × 1015 concept image. Native shell: 1100 × 700 device-independent pixels, captured in a 1200-wide transparent window canvas at 100%; focused shell crop is 2200 × 1400 at 200%.
- Collapsed reference: 2055 × 765 concept board including exterior padding. Compare the pill region, not that exterior canvas. Native compact shell: 480 × 68 device-independent pixels; final focused crop: 960 × 136 at 200%.
- Native fixture additionally renders 100%, 150%, and 200% density. Sizes above distinguish physical pixels from WPF layout units; this is a native app, not CSS/browser output.
- State: dark theme, Compare enabled, one shared binary-search question, Gemini and OpenAI complete. The fixture intentionally uses the short answers from the selected collapsed reference instead of the expanded concept's illustrative paragraphs.
- Small-screen test: 640 × 480 work area; cards stack inside a scroll region, with the shared composer and essential actions outside that region. Both cards are reachable. Full-view images clearly expose the provider controls; the compact 200% crop supplies focused text/spacing evidence.

### Fidelity review and intentional native-app adaptations

- Typography: existing Segoe UI Variable Text, 15.5px response body and 14px compact copy, readable blue provider labels and white answers. Long compact responses trim to one line with full-answer tooltips. The concept's scaled presentation is not treated as a literal font-size specification.
- Layout: equal answer cards, shared question, per-answer Copy/Retry and one composer. Existing Ask/Say, quick prompts, API keys, New question, and Quit Q remain available. Compare is a native on/off pill rather than introducing a second Single/Compare segmented component. Compact dimensions expand temporarily without overwriting ordinary island sizing preferences.
- Colors: existing near-black shell, slate card borders, blue controls/labels and light answer text; no decorative image gradients added.
- Assets: reuse the existing native Q branding and Segoe Fluent icon set. Provider text names are retained instead of adding the concept's decorative provider logos; no new raster artwork or imitated provider marks are introduced.
- Content: actual independent provider responses drive each label/answer row. Synthetic provider/model names in fixture captures are test data, not hardcoded production choices. The empty composer correctly disables Send to both. Screen-context disclosure explains both destinations and separate billing/account limits.

### Verification

- 151 unit tests pass, including parallel requests, one capture, isolated credentials/history, independent failures, retry, cancellation, and late-event suppression.
- Final native comparison harness: 26 assertions pass (`artifacts/compare-verification-v5/captures/checks.txt`).
- Existing provider/key UI regression harness passes (`artifacts/compare-provider-regression-final/captures/checks.txt`).
- Native comparison binding-warning log is empty. Release single-file publication succeeds.
- Test gap: verification uses local synthetic provider streams and credentials; no paid live API requests were sent. This is native WPF rendering and control verification, not desktop click automation.

## Remaining findings / checklist

No actionable P0/P1/P2 findings remain. P3 follow-up: optionally apply the app's dark scrollbar styling to the small-screen comparison scroller. The selected layout has been implemented within the existing native product, not published as a separate web prototype.

- [x] Resolve selected expanded and collapsed concept.
- [x] Implement independent provider responses and compact labeled lines.
- [x] Fix visual issues and repeat native captures.
- [x] Verify controls, persistence, cancellation, narrow-screen access, and single-provider regressions.
- [x] Inspect final native evidence and record remaining live-API test gap.
