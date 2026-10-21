# Browser-Coding-Agent

A .NET 10 Minimal API server exposing an OpenAI-compatible chat completions interface backed by Microsoft 365 Copilot web chat via Playwright for .NET.

The server opens a visible, standalone Microsoft Edge or Google Chrome window. Sign in manually and select your desired model and chat session in that window. Automation enters the prompt, clicks Send once, observes generation, and extracts the completed assistant response.

See [PLAN.md](PLAN.md) and [PACKAGING.md](PACKAGING.md) for architectural and packaging designs.

## Requirements

- .NET 10 SDK
- Google Chrome or Microsoft Edge installed on the host OS
- npm package only: Node 20 or newer (Playwright's driver runs on it)

## Build and test

Build the solution:

```sh
dotnet build
```

Run automated unit and simulated browser tests, excluding live M365 validation (also use this command in CI):

```sh
dotnet test --filter "Category!=ManualLiveValidation"
```

The live test has one toggle in [M365IntegrationTests.cs](tests/BrowserCodingAgent.Tests/M365IntegrationTests.cs):

```csharp
private const bool SkipLocal = false;
```

Set `SkipLocal = true` to skip it, or `false` to run it. It defaults to `false` and opens a visible browser with a persistent profile to send a prompt and await the response.

Run the live test:

```sh
dotnet test --filter "FullyQualifiedName~M365IntegrationTests"
```

Create .NET tool package:

```sh
dotnet pack src/BrowserCodingAgent.Api -c Release -o artifacts/nuget
```

Create and smoke-test the npm package (headless Chrome against a fake chat page):

```sh
node scripts/package-npm.mjs --version 0.0.0-local.1
node scripts/smoke.mjs --npm artifacts/npm/liqngliz-browser-coding-agent-0.0.0-local.1.tgz
```

## CI and release

| Workflow | Trigger | Does |
| --- | --- | --- |
| `ci.yml` | PR, push to `main` | `build.yml` with version `0.0.0-ci.<run>` |
| `build.yml` | called | tests (ubuntu/windows/macos), packs `.nupkg` + `.tgz` once, smoke-tests both on windows, macOS arm64, macOS x64, plus npm bootstrap in a container without .NET |
| `release.yml` | tag `v<semver>` | `build.yml`, then publishes the tested artifacts to nuget.org and npm (`latest`, or `next` for prerelease tags) |
| `post-release-smoke.yml` | after `release`, or manual | waits for nuget.org to serve the version, installs the tool from nuget.org, smoke-tests it with installed Edge on windows, macOS arm64, macOS x64 |

Release: `git tag v0.1.0 && git push origin v0.1.0`. Run without installing: `dnx BrowserCodingAgent` (or `dnx BrowserCodingAgent@0.1.0`). Publish jobs use the `release` environment and OIDC trusted publishing; no API keys are stored.

## Running locally

Run the API server:

```sh
dotnet run --project src/BrowserCodingAgent.Api
```

The server listens on `http://localhost:5000` (or configured loopback port) and launches the configured browser window.

## Configuration

Default `appsettings.json`:

```json
{
  "Browser": {
    "Channel": "chrome",
    "StartUrl": "https://m365.cloud.microsoft/chat"
  },
  "Chat": {
    "ModelAlias": "m365-active",
    "ResponseTimeoutMinutes": 10
  }
}
```

### Prompt replacements

`replacements.json` rewrites prompts before they reach Copilot and maps reversible rules back in replies. Default rules target Cline's system prompt (`You are Cline, an AI coding agent.`, `<user_input mode="...">`, `<mode_notice>`, `<env>`), whose persona and wrapper tags trip Copilot's guardrails.

| Field | Meaning |
| --- | --- |
| `find` / `replace` | text, or .NET regex when `regex: true` (`$1` = group) |
| `wholeWord` | default `true`; `Cline` does not match inside `decline` |
| `reverse` | map `replace` back to `find` in replies (plain text only) |

Lookup order: `Chat:ReplacementsFile` -> `~/.browser-coding-agent/replacements.json` -> bundled default (`src/BrowserCodingAgent.Api/replacements.json`). Restart to reload. Startup fails on invalid JSON or regex.

Persistent profiles default to `~/.browser-coding-agent/profiles/<channel>` outside package caches, keeping manual sign-in between restarts. Override with `Browser:UserDataDirectory` if desired.

## Endpoints

| Endpoint | Method | Description |
| --- | --- | --- |
| `/v1/chat/completions` | `POST` | Submit single user message; returns completed assistant turn |
| `/v1/models` | `GET` | Returns list containing configured alias (`m365-active`) |
| `/health/live` | `GET` | Reports if server process is running |
| `/health/ready` | `GET` | Reports if browser, tab, and composer are idle and ready (503 when busy or recovery needed) |
| `/swagger` | `GET` | Swagger UI, with Cline and OpenCode setup values for this server's URL and model |
| `/openapi/v1.json` | `GET` | OpenAPI 3.1 document |

## Supported API subset and limitations

- **Model:** Must match configured `Chat:ModelAlias` (`m365-active`).
- **Messages:** roles `system`, `developer`, `user`, `assistant`, `tool`. Roll-forward mapping onto the M365 tab:
  - no `assistant` message yet (first turn) -> new M365 chat, prompt = system text + user message, joined by blank lines
  - otherwise -> only the messages after the last `assistant` message (`user` and `tool` content, joined by blank lines)
  - no role tags added: Copilot's guardrails refuse prompts wrapped in `<system>` ("it looks like I can't chat about this"); client content is sent unchanged
  - client-side rewinds are not mirrored; revert files with git
- **Content:** String content or array of `{"type": "text", "text": "..."}` parts.
- **Streaming:** `stream: true` -> SSE `chat.completion.chunk` deltas, `data: [DONE]`, `: keep-alive` every 15s; `stream_options.include_usage` -> zero-valued usage chunk (M365 exposes no token counts). Errors after Send arrive as an in-stream `{"error": ...}` event.
- **Parameters:** `n: 1` only; `response_format` -> HTTP 400; legacy `functions`, `temperature`, and other generation settings are ignored (logged).
- **Tool calling (emulated):** `tools` are described in the prompt as plain text; fenced call blocks in Copilot's reply come back as `tool_calls` with `finish_reason: "tool_calls"` (streamed as one `tool_calls` delta). `tool_choice`: `none` disables tools, `required` or a named function adds a must-call line. `tool` result messages are sent as `Result of <tool name>:`. Copilot web chat has no native function calling, so calls depend on Copilot following the format.
- **Long prompts:** some M365 accounts cap the composer at 8000 characters. When the composer keeps only a prefix, or Send stays disabled on a prompt over 8000 characters, nothing is sent and the prompt is resent in parts in the same chat ("Part k of n … reply only with: received k/n"; the last part asks for the answer). Set `--Chat:MaxPromptChars=7000` on such accounts to split up front; default `0` splits only after a refusal. Each extra part uses one Copilot message.
- **Send resilience:** after filling, the server waits up to `Chat:SendEnableSeconds` (default 30) for Send. At 10s it focuses the composer and presses Enter; at two thirds it fills again. After each send it checks the composer: still holding exactly the prompt (and no Stop button, new message, banner, or URL change) means the send failed, so it retries (click, click, then focus + Enter). Any failure before a confirmed send (nothing reached Copilot) retries the whole submission up to 3 times, 2s then 4s apart, reloading the chat page before the last attempt; a confirmed send is never repeated. Before submitting, leftover composer text is cleared and an answer still generating is waited out (up to 60s) instead of returning 503.
- **Concurrency:** Single-request gate. Overlapping requests receive HTTP 409 Conflict.
- **Turn recovery:** Uncertain submissions or timeouts require manual recovery before accepting new prompts.
