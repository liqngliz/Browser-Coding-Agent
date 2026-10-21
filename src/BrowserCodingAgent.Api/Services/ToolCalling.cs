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

    // Full instructions on the first turn of a chat; later turns repeat the format and the exact names with parameters
    public static string BuildInstructions(IReadOnlyList<ToolSpec> tools, string? required, bool firstTurn)
    {
        // Explicit \n: AppendLine would write \r\n on Windows
        var sb = new StringBuilder();
        if (firstTurn)
        {
            sb.Append("How this conversation works").Append('\n');
            sb.Append("You are connected to an application running on the user's computer. You cannot open the user's files or run " +
                          "programs yourself, but the application can: when you write a tool call block, the application runs that tool " +
                          "on the user's computer and its next message contains the result. So never ask the user to upload, paste, or " +
                          "share files; request what you need with tool call blocks. Do not use your own code interpreter, sandbox, " +
                          "or file storage for this task.").Append('\n');
            sb.Append('\n');
            sb.Append("Write one fenced block per tool call, exactly in this form, with arguments as a JSON object that matches the tool's parameters:").Append('\n');
            sb.Append($"```{Fence}").Append('\n');
            sb.Append("{\"name\": \"<tool name>\", \"arguments\": {}}").Append('\n');
            sb.Append("```").Append('\n');
            sb.Append("For example:").Append('\n');
            sb.Append($"```{Fence}").Append('\n');
            sb.Append($"{{\"name\": \"{tools[0].Name}\", \"arguments\": {ExampleArguments(tools[0].ParametersJson)}}}").Append('\n');
            sb.Append("```").Append('\n');
            sb.Append("You may write a short explanation before the blocks. After the blocks, stop and wait for the results.").Append('\n');
            sb.Append("Instead of saying you cannot access a file or run a command, request it with a tool call block.").Append('\n');
            sb.Append('\n');
            sb.Append("Available tools:").Append('\n');
            foreach (var tool in tools)
            {
                sb.Append($"- {tool.Name}: {tool.Description?.Trim()}").Append('\n');
                sb.Append($"  Parameters (JSON schema): {tool.ParametersJson}").Append('\n');
            }
            sb.Append("Use only the tool names listed above, spelled exactly. Never invent other tool names.").Append('\n');
        }
        else
        {
            // Every later turn repeats the exact format and names: Copilot drifts to invented names otherwise
            sb.Append("Tool call format: one fenced block per call, exactly:").Append('\n');
            sb.Append($"```{Fence}").Append('\n');
            sb.Append("{\"name\": \"<tool name>\", \"arguments\": {}}").Append('\n');
            sb.Append("```").Append('\n');
            sb.Append($"Tool names, use exactly these with these parameters: {string.Join(", ", tools.Select(Signature))}. Never invent other tool names.").Append('\n');
            sb.Append("Request files, searches, and commands with these blocks; do not ask the user to upload or paste anything.").Append('\n');
        }

        if (required != null)
            sb.Append(required).Append('\n');

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

    public static (string Text, List<ToolCall> Calls) ParseReply(string reply, IReadOnlyCollection<string> toolNames) =>
        ParseReply(reply, toolNames, out _);

    // rejected: call-like blocks that stayed text, with the reason (unknown tool name, invalid JSON)
    public static (string Text, List<ToolCall> Calls) ParseReply(
        string reply, IReadOnlyCollection<string> toolNames, out List<string> rejected)
    {
        var calls = new List<ToolCall>();
        var rejections = new List<string>();

        // Any fence language: M365 relabels languages it does not know (tool_call shows as 'Shell').
        // Ordinary code stays text because a block must be valid JSON naming only requested tools.
        var text = FencedBlock.Replace(reply, match =>
        {
            var (parsed, rejection) = AnalyzeBlock(match.Groups["body"].Value, toolNames);
            if (rejection != null)
                rejections.Add(rejection);
            if (parsed.Count == 0)
                return match.Value;

            calls.AddRange(parsed);
            return string.Empty;
        });

        // No fences at all: the whole reply may be a bare call object
        if (calls.Count == 0 && rejections.Count == 0)
        {
            var (bare, rejection) = AnalyzeBlock(reply.Trim(), toolNames);
            if (bare.Count > 0)
            {
                rejected = rejections;
                return (string.Empty, bare);
            }
            if (rejection != null && reply.TrimStart().StartsWith('{'))
                rejections.Add(rejection);
        }

        rejected = rejections;
        return (text, calls);
    }

    private static readonly JsonReaderOptions LenientJson = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
        AllowMultipleValues = true // several call objects in one block, one after another
    };

    // All-or-nothing per block: every call must name a requested tool, or the whole block stays text.
    // Rejection is reported only for blocks that look like a call (they mention "name" or "tool").
    private static (List<ToolCall> Calls, string? Rejection) AnalyzeBlock(string body, IReadOnlyCollection<string> toolNames)
    {
        var looksLikeCall = body.Contains("\"name\"", StringComparison.Ordinal) || body.Contains("\"tool", StringComparison.Ordinal);
        var values = new List<JsonElement>();
        try
        {
            var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(body), LenientJson);
            while (reader.Read())
            {
                using var document = JsonDocument.ParseValue(ref reader);
                values.Add(document.RootElement.Clone());
            }
        }
        catch (JsonException ex)
        {
            return (new List<ToolCall>(), looksLikeCall ? $"invalid JSON ({ex.Message.Split('.')[0]})" : null);
        }

        var candidates = values.SelectMany(v => v.ValueKind == JsonValueKind.Array ? (IEnumerable<JsonElement>)v.EnumerateArray() : new[] { v }).ToList();
        if (candidates.Count == 0)
            return (new List<ToolCall>(), null);

        var calls = new List<ToolCall>();
        foreach (var candidate in candidates)
        {
            var (name, arguments) = ExtractCall(candidate);
            if (name == null)
                return (new List<ToolCall>(), looksLikeCall ? "no tool name" : null);
            if (!toolNames.Contains(name))
                return (new List<ToolCall>(), $"unknown tool '{name}'");

            calls.Add(new ToolCall($"call_{Guid.NewGuid():N}"[..29], "function", new ToolCallFunction(name, arguments)));
        }

        return (calls, null);
    }

    // {"name", "arguments"} (taught format), also {"function": {...}} (OpenAI), "tool"/"tool_name" for the name,
    // and "parameters"/"input"/"args" for the arguments
    private static (string? Name, string Arguments) ExtractCall(JsonElement candidate)
    {
        if (candidate.ValueKind != JsonValueKind.Object)
            return (null, "{}");

        if (candidate.TryGetProperty("function", out var function) && function.ValueKind == JsonValueKind.Object)
            candidate = function;

        string? name = null;
        foreach (var key in new[] { "name", "tool", "tool_name" })
        {
            if (candidate.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
            {
                name = value.GetString();
                break;
            }
        }

        foreach (var key in new[] { "arguments", "parameters", "input", "args" })
        {
            if (candidate.TryGetProperty(key, out var args))
                return (name, args.ValueKind == JsonValueKind.String ? args.GetString() ?? "{}" : JsonSerializer.Serialize(args, CompactJson));
        }

        return (name, "{}");
    }

    // read_files(files), editor(command, path, old_str?, new_str?): top-level parameters, optional ones marked '?'
    public static string Signature(ToolSpec tool)
    {
        try
        {
            var schema = JsonDocument.Parse(tool.ParametersJson).RootElement;
            if (!schema.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
                return $"{tool.Name}()";

            var required = schema.TryGetProperty("required", out var r) && r.ValueKind == JsonValueKind.Array
                ? r.EnumerateArray().Select(x => x.GetString()).ToHashSet()
                : new HashSet<string?>();
            var parameters = properties.EnumerateObject().Select(p => required.Contains(p.Name) || required.Count == 0 ? p.Name : p.Name + "?");
            return $"{tool.Name}({string.Join(", ", parameters)})";
        }
        catch (JsonException)
        {
            return $"{tool.Name}()";
        }
    }
}
