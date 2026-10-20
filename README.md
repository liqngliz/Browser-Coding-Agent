# Browser-Coding-Agent

A .NET 10 Minimal API server exposing an OpenAI-compatible chat completions interface backed by Microsoft 365 Copilot web chat via Playwright for .NET.

The server opens a visible, standalone Microsoft Edge or Google Chrome window. Sign in manually and select your desired model and chat session in that window. Automation enters the prompt, clicks Send once, observes generation, and extracts the completed assistant response.

See [PLAN.md](PLAN.md) and [PACKAGING.md](PACKAGING.md) for architectural and packaging designs.

## Requirements

- .NET 10 SDK
- Google Chrome or Microsoft Edge installed on the host OS

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

Release: `git tag v0.1.0 && git push origin v0.1.0`. Publish jobs use the `release` environment and OIDC trusted publishing; no API keys are stored.

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

Persistent profiles default to `~/.browser-coding-agent/profiles/<channel>` outside package caches, keeping manual sign-in between restarts. Override with `Browser:UserDataDirectory` if desired.

## Endpoints

| Endpoint | Method | Description |
| --- | --- | --- |
| `/v1/chat/completions` | `POST` | Submit single user message; returns completed assistant turn |
| `/v1/models` | `GET` | Returns list containing configured alias (`m365-active`) |
| `/health/live` | `GET` | Reports if server process is running |
| `/health/ready` | `GET` | Reports if browser, tab, and composer are idle and ready (503 when busy or recovery needed) |

## Supported API subset and limitations

- **Model:** Must match configured `Chat:ModelAlias` (`m365-active`).
- **Messages:** Single user message per request. Multi-turn history is managed directly within the active browser tab.
- **Content:** String content or array of `{"type": "text", "text": "..."}` parts.
- **Parameters:** `stream: false`, `n: 1`. Unsupported parameters (temperature, tools, streaming) return HTTP 400 with OpenAI error envelopes.
- **Concurrency:** Single-request gate. Overlapping requests receive HTTP 409 Conflict.
- **Turn recovery:** Uncertain submissions or timeouts require manual recovery before accepting new prompts.
