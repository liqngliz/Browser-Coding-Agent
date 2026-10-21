using System.Reflection;
using BrowserCodingAgent.Api.Configuration;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.Options;

namespace BrowserCodingAgent.Api.OpenApi;

public static class ClientSetupDescription
{
    private const string FallbackBaseUrl = "http://localhost:5000";

    // Package version (-p:Version), without the "+<commit>" source-link suffix
    private static readonly string AppVersion =
        (typeof(ClientSetupDescription).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0")
        .Split('+')[0];

    public static OpenApiOptions AddClientSetupDescription(this OpenApiOptions options)
    {
        options.AddDocumentTransformer((document, context, _) =>
        {
            var services = context.ApplicationServices;
            var baseUrl = ResolveBaseUrl(services.GetService<IServer>());
            var modelAlias = services.GetRequiredService<IOptions<ChatOptions>>().Value.ModelAlias;

            document.Info.Title = "Browser Coding Agent";
            document.Info.Version = AppVersion;
            document.Info.Description = Build(baseUrl, modelAlias);
            return Task.CompletedTask;
        });
        return options;
    }

    public static string Build(string baseUrl, string modelAlias) => $$"""
        OpenAI-compatible chat completions backed by the Microsoft 365 Copilot window this server controls.

        | Setting | Value |
        | --- | --- |
        | Base URL | `{{baseUrl}}/v1` |
        | Model | `{{modelAlias}}` |
        | API key | any non-empty value, e.g. `local` (not checked) |

        ## Working today

        ```sh
        curl {{baseUrl}}/v1/chat/completions \
          -H "Content-Type: application/json" \
          -d '{"model":"{{modelAlias}}","messages":[{"role":"user","content":"Hello"}]}'
        ```

        ## Coding agents

        | Client sends | Server does |
        | --- | --- |
        | `stream: true` | Server-Sent Events (`chat.completion.chunk`, `[DONE]`), keep-alive comments every 15s |
        | system prompt + history, no assistant message yet (first turn) | opens a new M365 chat, sends the system text + the user message as plain text (no role tags) |
        | history with assistant messages (later turns) | sends only the messages after the last assistant message |
        | `tools`, `temperature`, other generation settings | ignored; replies are plain text |

        Prompts pass through `replacements.json` first: by default `Cline` is sent as `Codewright` (mapped back in replies) and
        Cline's `<user_input>`, `<mode_notice>`, `<env>` wrappers are removed, because Copilot's guardrails refuse them.

        History is roll-forward only: rewinding in the client (checkpoint restore, undo) does not rewind the
        M365 chat. Revert files with git instead. One request at a time; a parallel request gets HTTP 409.

        ### Cline

        Settings → API Provider: **OpenAI Compatible**

        | Field | Value |
        | --- | --- |
        | Base URL | `{{baseUrl}}/v1` |
        | API Key | `local` |
        | Model ID | `{{modelAlias}}` |
        | Model Configuration → Image Support | off (text only) |
        | Model Configuration → Computer Use | off (no tool calling) |

        ### OpenCode

        `opencode.json` in the project root, then `/models` to select it:

        ```json
        {
          "$schema": "https://opencode.ai/config.json",
          "provider": {
            "browser-coding-agent": {
              "npm": "@ai-sdk/openai-compatible",
              "name": "M365 Copilot (Browser Coding Agent)",
              "options": {
                "baseURL": "{{baseUrl}}/v1",
                "apiKey": "local"
              },
              "models": {
                "{{modelAlias}}": { "name": "M365 Copilot" }
              }
            }
          },
          "model": "browser-coding-agent/{{modelAlias}}"
        }
        ```
        """;

    private static string ResolveBaseUrl(IServer? server)
    {
        var address = server?.Features.Get<IServerAddressesFeature>()?.Addresses
            .FirstOrDefault(a => a.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            ?? server?.Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault();

        if (address == null || !Uri.TryCreate(address.Replace("://+", "://localhost").Replace("://*", "://localhost"), UriKind.Absolute, out var uri))
            return FallbackBaseUrl;

        // Wildcard binds (0.0.0.0, [::]) are not reachable as typed; clients connect via localhost
        var host = uri.Host is "0.0.0.0" or "[::]" or "::" ? "localhost" : uri.Host;
        return $"{uri.Scheme}://{host}:{uri.Port}";
    }
}
