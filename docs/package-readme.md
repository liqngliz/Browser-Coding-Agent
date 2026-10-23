# Browser Coding Agent

OpenAI-compatible chat completions server backed by Microsoft 365 Copilot or Google Gemini web chat, for coding agents such as Cline.

## Run

| Package | Command | Needs |
| --- | --- | --- |
| npm | `npx -y @liqngliz/browser-coding-agent` | Node 20+, Chrome or Edge |
| .NET tool, no install | `dnx BrowserCodingAgent` | .NET 10 SDK, Chrome or Edge |
| .NET tool, installed | `dotnet tool install -g BrowserCodingAgent`, then `browser-coding-agent` | .NET 10 SDK, Chrome or Edge |

Specific version: `npx -y @liqngliz/browser-coding-agent@0.1.2`, `dnx BrowserCodingAgent@0.1.2`.

First start opens a browser window: sign in to Copilot (or Gemini) there. The sign-in is kept for later runs.

Gemini: add `--Chat:Provider=gemini`.

## Options

Arguments after the command go to the server:

```sh
npx -y @liqngliz/browser-coding-agent --urls http://localhost:5029 --Browser:Channel=msedge
dnx BrowserCodingAgent --urls http://localhost:5029 --Browser:Channel=msedge
```

| Option | Default | Meaning |
| --- | --- | --- |
| `--urls` | `http://localhost:5000` | listen address |
| `--Chat:Provider` | `m365` | `m365` or `gemini` |
| `--Browser:Channel` | `chrome` | `chrome` or `msedge` |
| `--Browser:UserDataDirectory` | `~/.browser-coding-agent/profiles/<channel>` (Gemini: `gemini-<channel>`) | browser profile (keeps the sign-in) |
| `--Chat:ResponseTimeoutMinutes` | `10` | wait per answer |
| `--Chat:MaxPromptChars` | `0` | split longer prompts into parts; `7000` for accounts with an 8000-character composer |
| `--Chat:SendEnableSeconds` | `30` | wait for Send after filling; focus + Enter at 10s |
| `--Chat:ReplacementsFile` | `~/.browser-coding-agent/replacements.json`, then bundled | prompt/reply replacement rules |

## Client setup (Cline)

| Field | Value |
| --- | --- |
| API Provider | OpenAI Compatible |
| Base URL | `http://localhost:5000/v1` (or your `--urls`) |
| API Key | any value, e.g. `local` |
| Model ID | `m365-active` (Gemini: `gemini-active`) |
| Context Window Size | M365: `1000000`; Gemini: `32000` free, `128000` AI Plus, `1000000` AI Pro / Ultra |

Setup details for the running server: `http://localhost:5000/swagger`.

Source, limitations, configuration: https://github.com/liqngliz/Browser-Coding-Agent
