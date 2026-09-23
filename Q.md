# Q visual assistant

Q is Dynamic Island’s on-demand visual assistant. Press the configured Q
activation shortcuts (`Ctrl+Alt+Q` by default) from another
application to capture the current context and ask a question without opening a
separate chat window. By default, Q expands the Island automatically. Disable
**Auto-expand island for Q** in **Settings → Q Assistant** if you want Q to stay
compact when it is invoked.

## Setup

1. Open **Settings → Q Assistant**.
2. Enable Q, select a provider and model, and add the provider API key when one is
   required.
3. Choose **Active window** or **Active monitor** as the capture source.
4. Decide whether to send the captured PNG to vision-capable models. OCR text is
   extracted from the capture for context.
5. Choose **Reasoning effort**. **Auto** uses the provider/model default; explicit
   values are sent when supported by the selected model.
6. Use **Test connection**, then invoke Q with the selected **Q activation
   shortcut**.

In **Settings → Q Assistant**, enable **Auto-close Q after response** if you want
completed Q sessions to close automatically. **Q auto-close delay** controls the
wait before closing, from 1 to 300 seconds; it is off by default.

### ChatGPT / Codex sign-in

The **ChatGPT / Codex** provider uses the official Codex app-server and does not
need an OpenAI API key. For the simplest test, download the release asset named
`DynamicIsland-Codex-Test-v1.0.6-win-x64.zip`, extract the whole folder, and run
`DynamicIsland.exe`. The normal `DynamicIsland.exe` remains available for users
who already have a supported official Codex installation.

In Q settings, choose **ChatGPT / Codex**, select **Sign in with ChatGPT**, and
enter the displayed device code at the official verification URL. The app-server
owns the OAuth login and token-refresh lifecycle; Dynamic Island receives account
status but never reads, copies, logs, or stores the OAuth token.

Runtime lookup is deterministic: a bundled and SHA-256-verified Codex 0.151.0
runtime is preferred, then a supported official per-user installation, then
`codex.exe` on `PATH`. A present but modified/incomplete bundle is rejected rather
than silently bypassed. The bundled ZIP is intentionally a test distribution so
the Codex version can be pinned while the app-server protocol evolves.

This provider uses Codex service access associated with the signed-in ChatGPT account,
including the account's Codex-specific model availability and rate limits. It does
not turn a ChatGPT subscription into a general OpenAI API key, and it does not use
API-platform billing or API credits. The API-key OpenAI provider remains available
separately.

The signed-in account is shared with official Codex apps on the same Windows user
profile. Signing out from Dynamic Island therefore signs that shared Codex profile
out everywhere; the UI warns before doing so. Model and effort choices come from
`model/list`, and Q passes the exact selected model and supported effort into each
turn. The displayed usage percentage and reset time come from Codex rate-limit
status. Access is subject to the account's eligible Codex subscription limits and
may vary by plan, model, region, rollout, and OpenAI policy.

Provider credentials are stored outside `settings.json` in a Windows user-scoped
DPAPI-protected file. They are not included in exported presets or repository
files. Ollama can be configured with its local base URL and does not require an
API key.

## Ask, Say, and shortcuts

- **Ask** answers, explains, solves, or analyzes the visible context.
- **Say** suggests concise first-person wording for what to say next; it does not
  claim that an action was taken.
- Type a prompt, use Windows dictation when available, or send follow-ups from
  the composer.
- Use **Stop**, **Copy**, **Retry**, **New question**, and **Quit Q** as the session
  state allows.
- Under **Q activation shortcuts**, enable any combination of `Ctrl+Alt+Q`,
  `Shift+A`, `Shift+comma`, `Shift+period`, and typing lowercase `var`.
  Changes are saved and apply immediately. Existing single-shortcut selections
  are preserved; new installations default to Ctrl+Alt+Q only.
- Type lowercase `v`, `a`, `r` consecutively in the same window within two
  seconds total. Uppercase or mixed-case input, other keys, and switching windows
  break the sequence. The typed letters remain in the text field. The listener
  tracks sequence progress only and does not record typed text.
- Disable **Enable Q** or uncheck a shortcut to release its keyboard registration.
- Create one-click prompt buttons under **Quick shortcuts** in Q settings. The
  **Hotkey quick action** setting can optionally run one of those shortcuts after
  the screen capture completes. Leave it as **None** to open Q without
  auto-submitting.

## Providers

The built-in provider registry supports OpenAI, Anthropic, Google Gemini, Groq,
xAI/Grok, OpenRouter, DeepSeek, and Ollama. Each adapter uses its provider's native
current contract instead of assuming that every API is interchangeable:

- OpenAI uses the [Responses API](https://developers.openai.com/api/reference/resources/responses/methods/create)
  with `max_output_tokens`, `reasoning.effort`, and Responses image parts.
- Anthropic uses the [Messages API](https://platform.claude.com/docs/en/api/messages/create)
  with `x-api-key`, `anthropic-version`, `output_config.effort`, and Anthropic SSE events.
- Gemini uses [`streamGenerateContent`](https://ai.google.dev/api/generate-content)
  with the key in `x-goog-api-key`, `thinkingConfig`, Gemini history roles, and `inlineData` images.
- Groq uses its [OpenAI-compatible chat endpoint](https://console.groq.com/docs/api-reference)
  with `max_completion_tokens` and model-supported reasoning effort.
- xAI uses [streaming Chat Completions](https://docs.x.ai/developers/model-capabilities/text/streaming)
  with Grok reasoning effort and image content.
- OpenRouter uses [Chat Completions](https://openrouter.ai/docs/quickstart), discovers
  model capabilities, and maps its per-model reasoning metadata.
- DeepSeek uses its [Chat Completions API](https://api-docs.deepseek.com/api/create-chat-completion/)
  with its thinking and reasoning controls; screenshots are sent only to a selected vision model.
- Ollama uses the native local [`/api/chat`](https://docs.ollama.com/api/chat) and
  `/api/tags` APIs, including NDJSON streaming, `think`, and base64 `images`.

Use the **Inference provider** dropdown in the Q panel to switch providers for your next question. Each provider remembers its model and reasoning effort, including across restarts. Switching is disabled while Q is capturing, listening, or answering.

### Compare two providers

Turn on **Compare** in the Q header, choose a different provider/model in each answer column, and use **Send to both**. Q captures the screen once per prompt and sends the same prompt and enabled screen context to both providers, using their separately saved credentials. Each provider uses its own API billing/account limits. Each answer streams independently; one provider failing does not discard the other answer. Follow-ups keep separate histories, never mixing the other provider's responses. Changing a provider/model resets comparison answers; **New question** clears both histories.

Expanded mode shows two independently scrollable answers with separate **Copy** and **Retry** actions. Small screens stack the answer panels in a scrollable area above the shared composer. **Stop both** cancels both requests. Retry affects only its answer and reuses that question's captured context while honoring the current image-sharing setting.

Collapsed comparison shows `Gemini: answer` and `OAI: answer` (labels follow the selected providers). Long answers are ellipsized; hovering a line shows its full text, and opening the island shows both full answers. Comparison temporarily reserves a wider 480-DIP compact island with two readable rows; turning Compare off restores the ordinary configured size. Compare mode and the second provider/model persist across restarts.

Choose **API keys** in Q (or open Settings → Q Assistant), select a provider, paste its key, and click **Save key**. Repeat for other providers: each keeps its own independently encrypted key. The saved-key summary lists configured providers without revealing credentials. **Remove key** only removes the selected provider's key. An empty input is not a missing-key indicator: saved credentials are never filled back into the editor. Codex uses ChatGPT sign-in and Ollama does not require an API key.

Model lists are refreshed by **Test connection** when a provider exposes discovery.
The model menu retains a current suggested default as an offline fallback. **Auto**
omits an explicit effort whenever possible so the provider/model default wins;
selecting another effort sends that exact supported value.

Provider APIs and model catalogs evolve independently. Preview models can be renamed
or retired, an account may not have every suggested model, and some models ignore or
reject effort/image fields they do not support. Use **Test connection** after changing
a provider or model. API-key providers use that vendor's API billing and rate limits;
they do not consume a ChatGPT subscription. Only **ChatGPT / Codex** uses eligible
Codex subscription limits.

## Privacy and data flow

Q captures only when invoked. The captured pixels, OCR text, and conversation
history remain in memory for the current request/session and are discarded when Q
is cleared or the application exits. If **Send screen image** is enabled and the
selected model supports images, the captured PNG is included in that provider
request; otherwise the request uses text/OCR context only.

Q does not provide a local model or its own account system. Choosing a hosted provider
means the prompt and any enabled screen image are sent to that provider. Review
the provider’s terms before using Q with sensitive windows, and avoid capturing
passwords, private messages, financial data, or other information you would not
send to that provider.

For Codex, Q creates a temporary isolated workspace under the app's local-data
folder, requests read-only operation with approvals disabled, rejects any tool or
interactive approval request, and deletes only the app-owned Codex thread after
the request. Temporary image files are deleted after the turn. Diagnostics are
local and sanitized: they can include runtime source/version, model, effort, and
failure category, but not prompts, screenshots, device codes, OAuth tokens, or
raw account credentials. These controls reduce risk; they do not make hosted
inference private from OpenAI.

## Troubleshooting

- If Q does not open, confirm **Enable Q** is on and that another application is
  not reserving the selected Q activation shortcut. `Shift+A` may also interfere
  with normal typing, so `Ctrl+Alt+Q` is recommended.
- If the capture is empty, retry with **Active monitor** or bring the source window
  to the foreground. Protected or minimized windows may not expose pixels.
- If a provider returns an error, verify the key, model, Ollama URL, and network
  access, then use **Retry** or **Test connection**.
- If the Codex provider cannot start, re-download and fully extract the bundled
  test ZIP, or install/update official Codex so a supported `codex.exe` is found.
- If sign-in expires, use the account control in Q settings or the expanded Island
  to sign in again. If a usage limit is reached, wait for the displayed reset time
  or use a different configured provider.
- Codex app-server is an evolving integration surface. Q currently uses it through
  local stdio and does not grant Codex command execution, file changes, or MCP
  actions from the Q flow.
- If a model responds without text, Q reports an actionable error instead of
  showing a misleading completed response.
