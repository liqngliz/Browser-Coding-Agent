using System.Text.Json;
using System.Text.Json.Serialization;
using BrowserCodingAgent.Api.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace BrowserCodingAgent.Api.Services;

// OpenAI chat.completion.chunk stream over Server-Sent Events.
// Writes are serialized (deltas and keep-alives come from different tasks) and never throw:
// after a client disconnect the browser turn still runs to completion.
public sealed class SseChatStream
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpResponse _response;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly string _id = $"chatcmpl-{Guid.NewGuid():N}";
    private readonly long _created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    private readonly string _model;

    public SseChatStream(HttpResponse response, string model)
    {
        _response = response;
        _model = model;
    }

    public bool Started { get; private set; }
    public bool ClientGone { get; private set; }

    public Task StartAsync() => WriteAsync(null);

    public Task WriteDeltaAsync(string content) =>
        WriteAsync(Data(Chunk(new ChatDelta(Content: content), null)));

    // One delta per call with complete arguments; OpenAI clients accept the whole call in a single chunk
    public Task WriteToolCallsAsync(IReadOnlyList<ToolCall> calls) =>
        WriteAsync(Data(Chunk(new ChatDelta(ToolCalls: calls
            .Select((c, i) => new ToolCallDelta(i, c.Id, c.Type, c.Function))
            .ToList()), null)));

    public Task WriteFinishAsync(string finishReason) =>
        WriteAsync(Data(Chunk(new ChatDelta(), finishReason)));

    public Task WriteUsageAsync() =>
        WriteAsync(Data(new ChatCompletionChunk(_id, "chat.completion.chunk", _created, _model, new List<ChatChunkChoice>(), new ChatUsage())));

    public Task WriteErrorAsync(ErrorResponse error) => WriteAsync(Data(error));

    public Task WriteDoneAsync() => WriteAsync("data: [DONE]\n\n");

    public Task WriteKeepAliveAsync() => WriteAsync(": keep-alive\n\n");

    private ChatCompletionChunk Chunk(ChatDelta delta, string? finishReason) =>
        new(_id, "chat.completion.chunk", _created, _model, new List<ChatChunkChoice> { new(0, delta, finishReason) });

    private static string Data<T>(T payload) => $"data: {JsonSerializer.Serialize(payload, JsonOptions)}\n\n";

    private async Task WriteAsync(string? payload)
    {
        await _writeLock.WaitAsync();
        try
        {
            if (ClientGone)
                return;

            if (!Started)
            {
                Started = true;
                _response.StatusCode = StatusCodes.Status200OK;
                _response.ContentType = "text/event-stream";
                _response.Headers.CacheControl = "no-cache";
                _response.HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
                await _response.WriteAsync(Data(Chunk(new ChatDelta(Role: "assistant", Content: string.Empty), null)), CancellationToken.None);
            }

            if (payload != null)
                await _response.WriteAsync(payload, CancellationToken.None);

            await _response.Body.FlushAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
            ClientGone = true;
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
