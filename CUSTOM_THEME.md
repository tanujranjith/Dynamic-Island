# Custom island theme

Open **Settings → Appearance → Theme → Custom**, then **Choose…** beside Custom background.

The picker offers a color preview, HEX input (`#RGB` or `#RRGGBB`), RGB sliders, swatches, and **Pick from screen…**. Click a screen pixel to sample; Escape or right-click cancels the eyedropper. **Use color** applies and saves the color. Cancel leaves the saved color unchanged.

Custom color affects the island surface and Q, including both comparison cards, shared composer, dropdowns and the two-line collapsed view. Text and secondary surface colors are generated for readable contrast. System, Light and Dark remain available; changing modes retains your custom color. Existing accent preferences and intentionally separate activity surfaces remain independent.

The eyedropper uses temporary in-memory screen images only while picking. They are disposed after selection or cancellation, never written to disk, and never passed to Q or an inference provider. On a capture error, HEX and RGB entry still work. Protected screen content may not be available for sampling.

## Verification

- 163 unit tests pass, including HEX validation and foreground contrast across a 4,096-color RGB sample.
- Native WPF fixtures cover dark/pale custom colors, Light/Dark switching, persistence, RGB and HEX controls, unchanged settings on picker dismissal, both comparison responses, and Settings picker visibility.
- Eyedropper sampling checks exact RGB with a negative monitor origin using synthetic pixels. Actual multi-monitor clicking and Escape handling need a user desktop check; no desktop image was captured during automated verification.
- Final fixture captures: `artifacts/theme-release/captures/` and `artifacts/theme-providers-release/captures/`.
