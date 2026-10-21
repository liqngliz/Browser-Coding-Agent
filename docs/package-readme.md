# Browser Coding Agent

OpenAI-compatible chat completions server backed by Microsoft 365 Copilot web chat, for coding agents such as Cline.

## Run

| Package | Command | Needs |
| --- | --- | --- |
| npm | `npx -y @liqngliz/browser-coding-agent` | Node 20+, Chrome or Edge |
| .NET tool, no install | `dnx BrowserCodingAgent` | .NET 10 SDK, Chrome or Edge |
| .NET tool, installed | `dotnet tool install -g BrowserCodingAgent`, then `browser-coding-agent` | .NET 10 SDK, Chrome or Edge |

Specific version: `npx -y @liqngliz/browser-coding-agent@0.1.2`, `dnx BrowserCodingAgent@0.1.2`.

First start opens a browser window: sign in to Copilot there. The sign-in is kept for later runs.

## Options

Arguments after the command go to the server:

```sh
npx -y @liqngliz/browser-coding-agent --urls http://localhost:5029 --Browser:Channel=msedge
dnx BrowserCodingAgent --urls http://localhost:5029 --Browser:Channel=msedge
```

| Option | Default | Meaning |
| --- | --- | --- |
| `--urls` | `http://localhost:5000` | listen address |
| `--Browser:Channel` | `chrome` | `chrome` or `msedge` |
| `--Browser:UserDataDirectory` | `~/.browser-coding-agent/profiles/<channel>` | browser profile (keeps the sign-in) |
| `--Chat:ResponseTimeoutMinutes` | `10` | wait per answer |
| `--Chat:MaxPromptChars` | `0` | split longer prompts into parts; `7000` for accounts with an 8000-character composer |
| `--Chat:ReplacementsFile` | `~/.browser-coding-agent/replacements.json`, then bundled | prompt/reply replacement rules |

## Client setup (Cline)

| Field | Value |
| --- | --- |
| API Provider | OpenAI Compatible |
| Base URL | `http://localhost:5000/v1` (or your `--urls`) |
| API Key | any value, e.g. `local` |
| Model ID | `m365-active` |

Setup details for the running server: `http://localhost:5000/swagger`.

Source, limitations, configuration: https://github.com/liqngliz/Browser-Coding-Agent
