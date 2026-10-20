using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BrowserCodingAgent.Api.Contracts;

namespace BrowserCodingAgent.Api.Services;

public record ToolSpec(string Name, string? Description, string ParametersJson);

// Tool calling emulated in plain text: M365 web chat has no function calling, so tool definitions are
// described in the prompt and ```tool_call fenced blocks in the reply are turned into OpenAI tool_calls.
public static class ToolCalling
{
    public const string Fence = "tool_call";

    private static readonly JsonSerializerOptions CompactJson = new() { WriteIndented = false };

    // DOM extraction renders code blocks as "```<lang>\n<code>\n```"; M365 may drop the language
    private static readonly Regex FencedBlock = new(
        @"```[ \t]*(?<lang>[\w-]*)[ \t]*\r?\n(?<body>.*?)\r?\n[ \t]*```",
        RegexOptions.Singleline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public static IReadOnlyList<ToolSpec> ParseDefinitions(object? tools)
    {
        if (tools == null)
            return Array.Empty<ToolSpec>();

        var root = tools is JsonElement element ? element : JsonSerializer.SerializeToElement(tools);
        if (root.ValueKind != JsonValueKind.Array)
            throw new FormatException("tools must be an array.");

        var specs = new List<ToolSpec>();
        foreach (var tool in root.EnumerateArray())
        {
            // OpenAI shape {"type":"function","function":{...}}; also accept a bare function object
            var function = tool.TryGetProperty("function", out var f) ? f : tool;
            if (!function.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(name.GetString()))
                throw new FormatException("Every tool needs function.name.");

            var description = function.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String
                ? d.GetString()
                : null;
            var parameters = function.TryGetProperty("parameters", out var p)
                ? JsonSerializer.Serialize(p, CompactJson)
                : "{}";
            specs.Add(new ToolSpec(name.GetString()!, description, parameters));
        }

        return specs;
    }

    // tool_choice "none" disables tools; "required" or a named function adds a must-call line
    public static (bool Enabled, string? Required) ParseChoice(JsonElement? toolChoice)
    {
        if (toolChoice is not { } choice || choice.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return (true, null);

        if (choice.ValueKind == JsonValueKind.String)
        {
            return choice.GetString() switch
            {
                "none" => (false, null),
                "required" => (true, "You must call at least one tool in this reply."),
                _ => (true, null)
            };
        }

        if (choice.ValueKind == JsonValueKind.Object &&
            choice.TryGetProperty("function", out var function) &&
            function.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
            return (true, $"You must call the tool {name.GetString()} in this reply.");

        return (true, null);
    }

    // Full instructions on the first turn of a chat; the M365 chat keeps them, later turns get the current tool names
    public static string BuildInstructions(IReadOnlyList<ToolSpec> tools, string? required, bool firstTurn)
    {
        var sb = new StringBuilder();
        if (firstTurn)
        {
            sb.AppendLine("How this conversation works");
            sb.AppendLine("You are connected to an application running on the user's computer. You cannot open the user's files or run " +
                          "programs yourself, but the application can: when you write a tool call block, the application runs that tool " +
                          "on the user's computer and its next message contains the result. So never ask the user to upload, paste, or " +
                          "share files; request what you need with tool call blocks. Do not use your own code interpreter, sandbox, " +
                          "or file storage for this task.");
            sb.AppendLine();
            sb.AppendLine("Write one fenced block per tool call, exactly in this form, with arguments as a JSON object that matches the tool's parameters:");
            sb.AppendLine($"```{Fence}");
            sb.AppendLine("{\"name\": \"<tool name>\", \"arguments\": {}}");
            sb.AppendLine("```");
            sb.AppendLine("For example:");
            sb.AppendLine($"```{Fence}");
            sb.AppendLine($"{{\"name\": \"{tools[0].Name}\", \"arguments\": {ExampleArguments(tools[0].ParametersJson)}}}");
            sb.AppendLine("```");
            sb.AppendLine("You may write a short explanation before the blocks. After the blocks, stop and wait for the results.");
            sb.AppendLine("Instead of saying you cannot access a file or run a command, request it with a tool call block.");
            sb.AppendLine();
            sb.AppendLine("Available tools:");
            foreach (var tool in tools)
            {
                sb.AppendLine($"- {tool.Name}: {tool.Description?.Trim()}");
                sb.AppendLine($"  Parameters (JSON schema): {tool.ParametersJson}");
            }
        }
        else
        {
            sb.AppendLine($"Tools available now: {string.Join(", ", tools.Select(t => t.Name))}. " +
                          $"Request files, searches, and commands with ```{Fence} blocks as described earlier; do not ask the user to upload or paste anything.");
        }

        if (required != null)
            sb.AppendLine(required);

        return sb.ToString().TrimEnd();
    }

    // Example arguments from a JSON schema: required properties filled with typed placeholders, nested one level
    private static string ExampleArguments(string parametersJson)
    {
        try
        {
            var schema = JsonDocument.Parse(parametersJson).RootElement;
            return JsonSerializer.Serialize(ExampleValue(schema, depth: 0), CompactJson);
        }
        catch (JsonException)
        {
            return "{}";
        }
    }

    private static object? ExampleValue(JsonElement schema, int depth)
    {
        var type = schema.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        if (schema.TryGetProperty("enum", out var values) && values.ValueKind == JsonValueKind.Array && values.GetArrayLength() > 0)
            return JsonSerializer.Deserialize<object>(values[0].GetRawText());

        switch (type)
        {
            case "object" when depth < 2:
                var example = new Dictionary<string, object?>();
                if (schema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object)
                {
                    var required = schema.TryGetProperty("required", out var r) && r.ValueKind == JsonValueKind.Array
                        ? r.EnumerateArray().Select(x => x.GetString()).ToHashSet()
                        : new HashSet<string?>();
                    foreach (var property in properties.EnumerateObject().Where(p => required.Count == 0 || required.Contains(p.Name)))
                        example[property.Name] = ExampleValue(property.Value, depth + 1);
                }
                return example;
            case "array":
                return schema.TryGetProperty("items", out var items) && depth < 2
                    ? new[] { ExampleValue(items, depth + 1) }
                    : Array.Empty<object>();
            case "integer" or "number":
                return 1;
            case "boolean":
                return true;
            case "string":
                return "...";
            default:
                return new Dictionary<string, object?>();
        }
    }

    // Streaming: show only text before the first fence; a block may be a tool call that must not reach the client as text
    public static string VisiblePrefix(string snapshot)
    {
        var fence = snapshot.IndexOf("```", StringComparison.Ordinal);
        return fence < 0 ? snapshot : snapshot[..fence];
    }

    public static (string Text, List<ToolCall> Calls) ParseReply(string reply, IReadOnlyCollection<string> toolNames)
    {
        var calls = new List<ToolCall>();

        // Any fence language: M365 relabels languages it does not know (tool_call shows as 'Shell').
        // Ordinary code stays text because a block must be valid JSON naming only requested tools.
        var text = FencedBlock.Replace(reply, match =>
        {
            var parsed = TryParseCalls(match.Groups["body"].Value, toolNames);
            if (parsed.Count == 0)
                return match.Value;

            calls.AddRange(parsed);
            return string.Empty;
        });

        // No fences at all: the whole reply may be a bare call object
        if (calls.Count == 0)
        {
            var bare = TryParseCalls(reply.Trim(), toolNames);
            if (bare.Count > 0)
                return (string.Empty, bare);
        }

        return (text, calls);
    }

    private static List<ToolCall> TryParseCalls(string body, IReadOnlyCollection<string> toolNames)
    {
        var calls = new List<ToolCall>();
        JsonElement root;
        try
        {
            root = JsonDocument.Parse(body).RootElement;
        }
        catch (JsonException)
        {
            return calls;
        }

        var candidates = root.ValueKind == JsonValueKind.Array ? root.EnumerateArray().ToList() : new List<JsonElement> { root };
        foreach (var candidate in candidates)
        {
            if (candidate.ValueKind != JsonValueKind.Object ||
                !candidate.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String ||
                !toolNames.Contains(name.GetString()!))
                return new List<ToolCall>(); // all-or-nothing: a block with an unknown tool stays text

            var arguments = candidate.TryGetProperty("arguments", out var args)
                ? args.ValueKind == JsonValueKind.String ? args.GetString() ?? "{}" : JsonSerializer.Serialize(args, CompactJson)
                : "{}";

            calls.Add(new ToolCall($"call_{Guid.NewGuid():N}"[..29], "function", new ToolCallFunction(name.GetString()!, arguments)));
        }

        return calls;
    }
}
