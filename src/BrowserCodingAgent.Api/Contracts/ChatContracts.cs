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

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
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
    [property: JsonPropertyName("content")] string? Content
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
