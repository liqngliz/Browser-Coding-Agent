using BrowserCodingAgent.Api.Contracts;

namespace BrowserCodingAgent.Api.Services;

// Preamble = system/developer text (first turn only); Input = the new user/tool messages
public record MappedTurn(string Prompt, bool IsFirstTurn, string Preamble = "", string Input = "");

public record MappingError(string Message, string Param, string Code);

// Roll-forward mapping onto the M365 tab, which keeps its own history:
//   first turn (no assistant message yet) -> system prompt + turn input as plain text, sent into a new chat
//   later turns                           -> only the messages after the last assistant message
// Client-side rewinds are not mirrored; the M365 chat keeps every turn it has seen.
public static class ConversationMapper
{
    public static (MappedTurn? Turn, MappingError? Error) Map(IReadOnlyList<ChatMessageRequest?>? messages)
    {
        if (messages == null || messages.Count == 0)
            return (null, new MappingError("At least one message is required.", "messages", "invalid_messages"));

        var lastAssistant = -1;
        for (var i = 0; i < messages.Count; i++)
        {
            var message = messages[i];
            if (message == null)
                return (null, new MappingError("Message entry cannot be null.", $"messages[{i}]", "invalid_messages"));

            switch (message.Role?.ToLowerInvariant())
            {
                case "assistant":
                    lastAssistant = i;
                    break;
                case "system" or "developer" or "user" or "tool":
                    break;
                default:
                    return (null, new MappingError(
                        $"Unsupported role '{message.Role}'. Expected system, developer, user, assistant, or tool.",
                        $"messages[{i}].role",
                        "invalid_role"));
            }
        }

        var isFirstTurn = lastAssistant < 0;
        var systemParts = new List<string>();
        var turnParts = new List<string>();

        // tool_call_id -> tool name, so each result says which call it answers
        var toolNamesById = messages
            .Where(m => m!.ToolCalls != null)
            .SelectMany(m => m!.ToolCalls!)
            .GroupBy(c => c.Id)
            .ToDictionary(g => g.Key, g => g.Last().Function.Name);

        for (var i = 0; i < messages.Count; i++)
        {
            var role = messages[i]!.Role!.ToLowerInvariant();
            var isSystem = role is "system" or "developer";

            if (role == "assistant" || (isSystem && !isFirstTurn) || (!isSystem && i < lastAssistant))
                continue;

            var text = messages[i]!.ExtractTextContent(out var contentError);
            if (contentError != null)
                return (null, new MappingError(contentError, $"messages[{i}].content", "invalid_content"));

            if (string.IsNullOrWhiteSpace(text))
            {
                if (role == "user")
                    return (null, new MappingError("Message content cannot be empty.", $"messages[{i}].content", "empty_content"));
                if (role != "tool")
                    continue;
                text = "(no output)";
            }

            if (isSystem)
                systemParts.Add(text.Trim());
            else if (role == "tool")
                turnParts.Add(messages[i]!.ToolCallId is { } id && toolNamesById.TryGetValue(id, out var toolName)
                    ? $"Result of {toolName}:\n{text.Trim()}"
                    : $"Tool result:\n{text.Trim()}");
            else
                turnParts.Add(text.Trim());
        }

        if (turnParts.Count == 0)
            return (null, new MappingError("No user or tool message follows the last assistant message.", "messages", "no_new_message"));

        // Plain text only: Copilot's guardrails refuse prompts wrapped in role tags such as <system>.
        // Client content is sent unchanged (Cline's own XML tool syntax must survive).
        var prompt = string.Join("\n\n", systemParts.Concat(turnParts));

        return (new MappedTurn(prompt, isFirstTurn, string.Join("\n\n", systemParts), string.Join("\n\n", turnParts)), null);
    }
}
