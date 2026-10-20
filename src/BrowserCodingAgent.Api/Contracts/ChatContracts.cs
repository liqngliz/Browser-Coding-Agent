using System.Text.Json;
using System.Text.Json.Serialization;

namespace BrowserCodingAgent.Api.Contracts;

public class ChatCompletionRequest
{
    public ChatCompletionRequest() { }

    public ChatCompletionRequest(
        string? model,
        List<ChatMessageRequest?>? messages,
        bool? stream = false,
        int? n = 1,
        object? tools = null,
        object? functions = null,
        object? responseFormat = null)
    {
        Model = model;
        Messages = messages;
        Stream = stream;
        N = n;
        Tools = tools;
        Functions = functions;
        ResponseFormat = responseFormat;
    }

    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("messages")]
    public List<ChatMessageRequest?>? Messages { get; set; }

    [JsonPropertyName("stream")]
    public bool? Stream { get; set; } = false;

    [JsonPropertyName("n")]
    public int? N { get; set; } = 1;

    [JsonPropertyName("tools")]
    public object? Tools { get; set; }

    [JsonPropertyName("functions")]
    public object? Functions { get; set; }

    [JsonPropertyName("response_format")]
    public object? ResponseFormat { get; set; }

    [JsonPropertyName("stream_options")]
    public StreamOptionsRequest? StreamOptions { get; set; }

    // "auto" | "none" | "required" | {"type":"function","function":{"name":...}}
    [JsonPropertyName("tool_choice")]
    public JsonElement? ToolChoice { get; set; }

    [JsonPropertyName("parallel_tool_calls")]
    public bool? ParallelToolCalls { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public class StreamOptionsRequest
{
    [JsonPropertyName("include_usage")]
    public bool? IncludeUsage { get; set; }
}

public class ChatMessageRequest
{
    public ChatMessageRequest() { }

    public ChatMessageRequest(string? role, string? textContent)
    {
        Role = role;
        if (textContent != null)
        {
            Content = JsonSerializer.SerializeToElement(textContent);
        }
    }

    [JsonPropertyName("role")]
    public string? Role { get; set; }

    [JsonPropertyName("content")]
    public JsonElement Content { get; set; }

    // Assistant messages: calls the model made; tool messages: which call this result answers
    [JsonPropertyName("tool_calls")]
    public List<ToolCall>? ToolCalls { get; set; }

    [JsonPropertyName("tool_call_id")]
    public string? ToolCallId { get; set; }

    public string? ExtractTextContent(out string? error)
    {
        error = null;
        if (Content.ValueKind == JsonValueKind.String)
        {
            return Content.GetString();
        }

        if (Content.ValueKind == JsonValueKind.Array)
        {
            var parts = new List<string>();
            foreach (var element in Content.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                {
                    error = "Content array elements must be objects with 'type' and 'text'.";
                    return null;
                }

                if (!element.TryGetProperty("type", out var typeProp) ||
                    typeProp.ValueKind != JsonValueKind.String ||
                    typeProp.GetString() != "text")
                {
                    error = "Only content parts of type 'text' are supported. Images and other parts are not supported.";
                    return null;
                }

                if (!element.TryGetProperty("text", out var textProp) ||
                    textProp.ValueKind != JsonValueKind.String)
                {
                    error = "Content part 'text' must be a string.";
                    return null;
                }

                parts.Add(textProp.GetString() ?? string.Empty);
            }

            return string.Join("\n", parts);
        }

        if (Content.ValueKind == JsonValueKind.Null || Content.ValueKind == JsonValueKind.Undefined)
        {
            return null;
        }

        error = "Message content must be a string or an array of text parts.";
        return null;
    }
}

public record ChatMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string? Content,
    [property: JsonPropertyName("tool_calls")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] List<ToolCall>? ToolCalls = null
);

public record ToolCall(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("function")] ToolCallFunction Function
);

public record ToolCallFunction(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("arguments")] string Arguments
);

public record ToolCallDelta(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("function")] ToolCallFunction Function
);

public record ChatCompletionResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("created")] long Created,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("choices")] List<ChatChoice> Choices
);

public record ChatChoice(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("message")] ChatMessage Message,
    [property: JsonPropertyName("finish_reason")] string FinishReason
);

public record ChatCompletionChunk(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("created")] long Created,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("choices")] List<ChatChunkChoice> Choices,
    [property: JsonPropertyName("usage")] ChatUsage? Usage = null
);

public record ChatChunkChoice(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("delta")] ChatDelta Delta,
    [property: JsonPropertyName("finish_reason")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? FinishReason
);

public record ChatDelta(
    [property: JsonPropertyName("role")] string? Role = null,
    [property: JsonPropertyName("content")] string? Content = null,
    [property: JsonPropertyName("tool_calls")] List<ToolCallDelta>? ToolCalls = null
);

// M365 exposes no token counts
public record ChatUsage(
    [property: JsonPropertyName("prompt_tokens")] int PromptTokens = 0,
    [property: JsonPropertyName("completion_tokens")] int CompletionTokens = 0,
    [property: JsonPropertyName("total_tokens")] int TotalTokens = 0
);

public record ModelsResponse(
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("data")] List<ModelData> Data
);

public record ModelData(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("created")] long Created,
    [property: JsonPropertyName("owned_by")] string OwnedBy
);

public record ErrorResponse(
    [property: JsonPropertyName("error")] ErrorDetail Error
);

public record ErrorDetail(
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("param")] string? Param = null,
    [property: JsonPropertyName("code")] string? Code = null
);

public record HealthLiveResponse(
    [property: JsonPropertyName("status")] string Status
);

public record HealthReadyResponse(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("reason")] string? Reason = null
);
