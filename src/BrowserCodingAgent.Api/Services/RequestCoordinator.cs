using BrowserCodingAgent.Api.Browser;
using BrowserCodingAgent.Api.Configuration;
using BrowserCodingAgent.Api.Contracts;
using BrowserCodingAgent.Api.Providers.M365;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BrowserCodingAgent.Api.Services;

public enum ChatTurnState
{
    Idle,
    Submitting,
    Generating,
    UncertainRecoveryRequired
}

public class RequestCoordinator
{
    private readonly IBrowserSession _browserSession;
    private readonly M365ChatAdapter _adapter;
    private readonly ChatOptions _chatOptions;
    private readonly ILogger<RequestCoordinator> _logger;
    private readonly PromptReplacements _replacements;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ReadinessRecoveryWait = TimeSpan.FromSeconds(60);
    private const int MaxSubmitAttempts = 3;

    public RequestCoordinator(
        IBrowserSession browserSession,
        M365ChatAdapter adapter,
        IOptions<ChatOptions> chatOptions,
        ILogger<RequestCoordinator> logger,
        PromptReplacements? replacements = null)
    {
        _browserSession = browserSession;
        _adapter = adapter;
        _chatOptions = chatOptions.Value;
        _logger = logger;
        _replacements = replacements ?? PromptReplacements.None;
    }

    public ChatTurnState CurrentState { get; private set; } = ChatTurnState.Idle;
    public string? RecoveryReason { get; private set; }

    public async Task<(bool Ready, string? Reason)> CheckCoordinatorReadyAsync()
    {
        if (CurrentState == ChatTurnState.Submitting || CurrentState == ChatTurnState.Generating)
            return (false, "Chat is currently processing an active request.");

        if (CurrentState == ChatTurnState.UncertainRecoveryRequired)
        {
            // Guarded auto-recovery: check if browser page returned to confirmed idle
            if (_browserSession.IsRunning)
            {
                var (pageReady, _) = await _browserSession.CheckReadyAsync(_adapter);
                if (pageReady)
                {
                    _logger.LogInformation("Browser chat confirmed idle after previous uncertain turn. Cleared recovery state.");
                    CurrentState = ChatTurnState.Idle;
                    RecoveryReason = null;
                    return (true, null);
                }
            }

            return (false, RecoveryReason ?? "Manual recovery required in browser window.");
        }

        return (true, null);
    }

    public async Task<IResult> ExecuteChatCompletionAsync(ChatCompletionRequest request, HttpContext httpContext)
    {
        // 1. Input validation before any browser interaction
        if (string.IsNullOrWhiteSpace(request.Model) ||
            !string.Equals(request.Model, _chatOptions.ModelAlias, StringComparison.OrdinalIgnoreCase))
        {
            return Results.Json(
                new ErrorResponse(new ErrorDetail(
                    $"The model '{request.Model}' does not exist or is not supported. Use '{_chatOptions.ModelAlias}'.",
                    "invalid_request_error",
                    "model",
                    "model_not_found")),
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (request.N.HasValue && request.N.Value != 1)
        {
            return Results.Json(
                new ErrorResponse(new ErrorDetail(
                    $"Only n = 1 is supported. Got n = {request.N.Value}.",
                    "invalid_request_error",
                    "n",
                    "unsupported_parameter")),
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (request.ResponseFormat != null)
        {
            return Results.Json(
                new ErrorResponse(new ErrorDetail(
                    "Structured output formatting (response_format) is not supported.",
                    "invalid_request_error",
                    "response_format",
                    "unsupported_feature")),
                statusCode: StatusCodes.Status400BadRequest);
        }

        // Tools are emulated in text (see ToolCalling); legacy "functions" is not supported and is ignored
        IReadOnlyList<ToolSpec> tools;
        try
        {
            tools = ToolCalling.ParseDefinitions(request.Tools);
        }
        catch (FormatException fex)
        {
            return Results.Json(
                new ErrorResponse(new ErrorDetail(fex.Message, "invalid_request_error", "tools", "invalid_tools")),
                statusCode: StatusCodes.Status400BadRequest);
        }

        var (toolsEnabled, requiredToolLine) = ToolCalling.ParseChoice(request.ToolChoice);
        if (tools.Count > 0)
            _logger.LogInformation("Request tools: {Tools}", string.Join(", ", tools.Select(t => t.Name)));
        if (!toolsEnabled)
            tools = Array.Empty<ToolSpec>();

        if (request.Functions != null)
            _logger.LogWarning("Request includes legacy 'functions'; only 'tools' are supported, the reply is plain text.");

        if (request.AdditionalProperties is { Count: > 0 })
            _logger.LogInformation("Ignoring unsupported parameters: {Parameters}", string.Join(", ", request.AdditionalProperties.Keys));

        var (turn, mappingError) = ConversationMapper.Map(request.Messages);
        if (mappingError != null)
        {
            return Results.Json(
                new ErrorResponse(new ErrorDetail(
                    mappingError.Message,
                    "invalid_request_error",
                    mappingError.Param,
                    mappingError.Code)),
                statusCode: StatusCodes.Status400BadRequest);
        }

        // 2. Concurrency gate check (immediate 409 without queuing)
        if (!await _gate.WaitAsync(0))
        {
            return Results.Json(
                new ErrorResponse(new ErrorDetail(
                    "A request is currently in progress. Concurrent requests are not supported.",
                    "conflict_error",
                    null,
                    "concurrent_request")),
                statusCode: StatusCodes.Status409Conflict);
        }

        try
        {
            // 3. Check recovery state with guarded auto-recovery check
            if (CurrentState == ChatTurnState.UncertainRecoveryRequired)
            {
                if (_browserSession.IsRunning)
                {
                    var (pageReady, _) = await _browserSession.CheckReadyAsync(_adapter);
                    if (pageReady)
                    {
                        _logger.LogInformation("Browser chat confirmed idle after previous uncertain turn. Cleared recovery state.");
                        CurrentState = ChatTurnState.Idle;
                        RecoveryReason = null;
                    }
                }

                if (CurrentState == ChatTurnState.UncertainRecoveryRequired)
                {
                    return Results.Json(
                        new ErrorResponse(new ErrorDetail(
                            $"Chat requires manual recovery before accepting new prompts: {RecoveryReason}",
                            "service_unavailable_error",
                            null,
                            "manual_recovery_required")),
                        statusCode: StatusCodes.Status503ServiceUnavailable);
                }
            }

            // 4. Browser readiness check
            var (ready, reason) = await _browserSession.CheckReadyAsync(_adapter);
            if (!ready && _browserSession.ActivePage != null && IsRecoverable(reason))
            {
                _logger.LogWarning("Browser not ready ({Reason}); recovering before submitting.", reason);
                (ready, reason) = await _adapter.RecoverToReadyAsync(_browserSession.ActivePage, ReadinessRecoveryWait, httpContext.RequestAborted);
            }

            if (!ready)
            {
                return Results.Json(
                    new ErrorResponse(new ErrorDetail(
                        $"Browser is not ready to accept chat prompts: {reason}",
                        "service_unavailable_error",
                        null,
                        "browser_not_ready")),
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            if (httpContext.RequestAborted.IsCancellationRequested)
            {
                return Results.Json(
                    new ErrorResponse(new ErrorDetail(
                        "Client cancelled request before submission.",
                        "invalid_request_error",
                        null,
                        "request_cancelled")),
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var timeout = TimeSpan.FromMinutes(_chatOptions.ResponseTimeoutMinutes);
            var page = _browserSession.ActivePage!;

            // Streamed requests write the response here, inside the gate; headers go out once Send is clicked
            var stream = request.Stream == true ? new SseChatStream(httpContext.Response, _chatOptions.ModelAlias) : null;
            var emitter = new StreamingTextEmitter();
            var sendDispatched = false;
            using var keepAliveCts = new CancellationTokenSource();
            var keepAlive = Task.CompletedTask;

            async Task<IResult> Fail(int statusCode, ErrorResponse error)
            {
                if (stream == null || !sendDispatched)
                    return Results.Json(error, statusCode: statusCode);

                await keepAliveCts.CancelAsync();
                await keepAlive;
                await stream.WriteErrorAsync(error);
                return Results.Empty;
            }

            CurrentState = ChatTurnState.Submitting;

            try
            {
                if (turn!.IsFirstTurn)
                    await _adapter.StartNewChatAsync(page, httpContext.RequestAborted);

                // First turn: preamble, then tool instructions, then the request (Copilot answers the request with
                // the tools already in mind); later turns: the new messages, then a short reminder
                var prompt = tools.Count == 0
                    ? turn.Prompt
                    : turn.IsFirstTurn
                        ? string.Join("\n\n", new[] { turn.Preamble, ToolCalling.BuildInstructions(tools, requiredToolLine, firstTurn: true), $"Request:\n{turn.Input}" }
                            .Where(p => !string.IsNullOrWhiteSpace(p)))
                        : $"{turn.Input}\n\n{ToolCalling.BuildInstructions(tools, requiredToolLine, firstTurn: false)}";

                var rawAnswer = await SendPromptAsync(
                    page,
                    _replacements.PrepareOutgoing(prompt),
                    timeout,
                    httpContext.RequestAborted,
                    onSendDispatched: () =>
                    {
                        // Once per request: chunked prompts dispatch one Send per part
                        if (sendDispatched)
                            return;
                        CurrentState = ChatTurnState.Generating;
                        sendDispatched = true;
                        if (stream != null)
                            keepAlive = KeepAliveAsync(stream, keepAliveCts.Token);
                    },
                    onProgress: stream == null
                        ? null
                        : async snapshot =>
                        {
                            var visible = _replacements.ApplyIncoming(snapshot);
                            if (tools.Count > 0)
                                visible = ToolCalling.VisiblePrefix(visible);

                            var delta = emitter.Next(visible);
                            if (delta.Length > 0)
                                await stream.WriteDeltaAsync(delta);
                        });

                CurrentState = ChatTurnState.Idle;
                RecoveryReason = null;
                var answer = _replacements.ApplyIncoming(rawAnswer);

                var toolCalls = new List<ToolCall>();
                if (tools.Count > 0)
                {
                    (answer, toolCalls) = ToolCalling.ParseReply(answer, tools.Select(t => t.Name).ToHashSet(StringComparer.Ordinal), out var rejected);
                    foreach (var rejection in rejected)
                        _logger.LogWarning("Reply contains a call-like block that stayed text: {Reason}. Requested tools: {Tools}",
                            rejection, string.Join(", ", tools.Select(t => t.Name)));
                    if (toolCalls.Count > 0)
                        _logger.LogInformation("Reply contains {Count} tool call(s): {Names}",
                            toolCalls.Count, string.Join(", ", toolCalls.Select(c => c.Function.Name)));
                }

                var finishReason = toolCalls.Count > 0 ? "tool_calls" : "stop";

                if (stream == null)
                {
                    var content = answer.Trim();
                    var response = new ChatCompletionResponse(
                        Id: $"chatcmpl-{Guid.NewGuid():N}",
                        Object: "chat.completion",
                        Created: DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        Model: _chatOptions.ModelAlias,
                        Choices: new List<ChatChoice>
                        {
                            new(
                                Index: 0,
                                Message: new ChatMessage(
                                    "assistant",
                                    content.Length == 0 && toolCalls.Count > 0 ? null : content,
                                    toolCalls.Count > 0 ? toolCalls : null),
                                FinishReason: finishReason)
                        });

                    return Results.Ok(response);
                }

                await keepAliveCts.CancelAsync();
                await keepAlive;

                // Untrimmed, so it still extends what was streamed
                var rest = emitter.Complete(answer);
                if (emitter.FinalMismatch)
                    _logger.LogWarning("Final answer does not extend the streamed text; the client received a truncated answer ({Streamed} of {Total} chars).",
                        emitter.Emitted.Length, answer.Length);
                if (rest.TrimEnd().Length > 0)
                    await stream.WriteDeltaAsync(rest.TrimEnd());

                if (toolCalls.Count > 0)
                    await stream.WriteToolCallsAsync(toolCalls);

                await stream.WriteFinishAsync(finishReason);
                if (request.StreamOptions?.IncludeUsage == true)
                    await stream.WriteUsageAsync();
                await stream.WriteDoneAsync();

                if (stream.ClientGone)
                    _logger.LogWarning("Client disconnected during streaming; the browser turn completed anyway.");

                return Results.Empty;
            }
            catch (OperationCanceledException) when (httpContext.RequestAborted.IsCancellationRequested && CurrentState == ChatTurnState.Submitting)
            {
                CurrentState = ChatTurnState.Idle;
                return await Fail(StatusCodes.Status400BadRequest,
                    new ErrorResponse(new ErrorDetail(
                        "Client cancelled request before submission.",
                        "invalid_request_error",
                        null,
                        "request_cancelled")));
            }
            catch (TimeoutException tex)
            {
                if (CurrentState == ChatTurnState.Generating)
                {
                    CurrentState = ChatTurnState.UncertainRecoveryRequired;
                    RecoveryReason = "Request timed out while generating. Manual verification in browser is required.";
                }
                else
                {
                    CurrentState = ChatTurnState.Idle;
                }

                _logger.LogWarning(tex, "Deadline expired waiting for browser answer.");
                return await Fail(StatusCodes.Status504GatewayTimeout,
                    new ErrorResponse(new ErrorDetail(
                        tex.Message,
                        "timeout_error",
                        null,
                        "deadline_exceeded")));
            }
            catch (UpstreamUiException uex)
            {
                if (CurrentState == ChatTurnState.Generating)
                {
                    CurrentState = ChatTurnState.UncertainRecoveryRequired;
                    RecoveryReason = $"Upstream Copilot error: {uex.Message}.";
                }
                else
                {
                    CurrentState = ChatTurnState.Idle;
                }

                _logger.LogError(uex, "Upstream UI reported error: {Message}", uex.Message);
                return await Fail(StatusCodes.Status502BadGateway,
                    new ErrorResponse(new ErrorDetail(
                        $"Upstream error from Copilot UI: {uex.Message}",
                        "api_error",
                        null,
                        "upstream_error")));
            }
            catch (InvalidOperationException iex)
            {
                if (CurrentState == ChatTurnState.Generating)
                {
                    CurrentState = ChatTurnState.UncertainRecoveryRequired;
                    RecoveryReason = $"Operation error: {iex.Message}.";
                }
                else
                {
                    CurrentState = ChatTurnState.Idle;
                }

                _logger.LogWarning(iex, "Invalid operation during chat interaction: {Message}", iex.Message);
                return await Fail(StatusCodes.Status409Conflict,
                    new ErrorResponse(new ErrorDetail(
                        iex.Message,
                        "conflict_error",
                        null,
                        "invalid_operation")));
            }
            catch (Exception ex)
            {
                if (CurrentState == ChatTurnState.Generating)
                {
                    CurrentState = ChatTurnState.UncertainRecoveryRequired;
                    RecoveryReason = $"Unexpected error: {ex.Message}.";
                }
                else
                {
                    CurrentState = ChatTurnState.Idle;
                }

                _logger.LogError(ex, "Unexpected error in chat completion: {Message}", ex.Message);
                return await Fail(StatusCodes.Status500InternalServerError,
                    new ErrorResponse(new ErrorDetail(
                        ex.Message,
                        "api_error",
                        null,
                        "internal_error")));
            }
            finally
            {
                await keepAliveCts.CancelAsync();
                await keepAlive;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    // One message, or parts when the prompt exceeds Chat:MaxPromptChars or the composer refuses it before Send
    // (some accounts cap it at 8000 characters). Parts share the chat; only the last part's reply is the answer.
    private async Task<string> SendPromptAsync(
        Microsoft.Playwright.IPage page,
        string prompt,
        TimeSpan timeout,
        CancellationToken cancellation,
        Action onSendDispatched,
        Func<string, Task>? onProgress)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        TimeSpan Remaining()
        {
            var remaining = timeout - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
                throw new TimeoutException($"Response timeout of {timeout.TotalMinutes} minutes exceeded while sending a multi-part prompt.");
            return remaining;
        }

        var limit = _chatOptions.MaxPromptChars;
        if (limit == 0 || prompt.Length <= limit)
        {
            try
            {
                return await SendWithRetryAsync(page, prompt, Remaining, cancellation, onSendDispatched, onProgress);
            }
            catch (PromptTooLongException tooLong)
            {
                // Nothing was sent and the composer was cleared; stay under what it accepted
                limit = tooLong.AcceptedLength is { } accepted
                    ? Math.Max(PromptChunker.MinChunkChars, Math.Min(limit == 0 ? PromptChunker.DefaultLimit : limit, accepted - 500))
                    : limit == 0 ? PromptChunker.DefaultLimit : limit;
                _logger.LogWarning("{Reason} Resending in parts of at most {Limit} characters.", tooLong.Message, limit);
            }
        }

        var parts = PromptChunker.Wrap(prompt, limit);
        _logger.LogInformation("Sending a {Length}-character prompt in {Parts} parts (limit {Limit}).", prompt.Length, parts.Count, limit);
        for (var i = 0; i < parts.Count - 1; i++)
        {
            var acknowledgement = await SendWithRetryAsync(page, parts[i], Remaining, cancellation, onSendDispatched, null);
            _logger.LogInformation("Part {Part}/{Total} acknowledged: {Reply}", i + 1, parts.Count,
                acknowledgement.Length > 80 ? acknowledgement[..80] + "..." : acknowledgement);
        }

        return await SendWithRetryAsync(page, parts[^1], Remaining, cancellation, onSendDispatched, onProgress);
    }

    // A failure before the send is confirmed means nothing reached Copilot, so the submission is retried: 2s then 4s
    // apart, with a page reload before the last attempt. Confirmed sends are never repeated; composer limits
    // (PromptTooLongException, handled by chunking) and usage limits are not retried here.
    private async Task<string> SendWithRetryAsync(
        Microsoft.Playwright.IPage page,
        string text,
        Func<TimeSpan> remaining,
        CancellationToken cancellation,
        Action onSendDispatched,
        Func<string, Task>? onProgress)
    {
        for (var attempt = 1; ; attempt++)
        {
            var dispatched = false;
            try
            {
                return await _adapter.SendAndObserveAsync(page, text, remaining(), cancellation,
                    () => { dispatched = true; onSendDispatched(); }, onProgress);
            }
            catch (InvalidOperationException ex) when (
                ex is not PromptTooLongException && !dispatched && attempt < MaxSubmitAttempts && !cancellation.IsCancellationRequested)
            {
                _logger.LogWarning("Submission attempt {Attempt}/{MaxAttempts} failed before anything was sent: {Reason} Retrying.",
                    attempt, MaxSubmitAttempts, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), cancellation);
                if (attempt == MaxSubmitAttempts - 1)
                    await _adapter.ReloadAsync(page, cancellation);
                await _adapter.RecoverToReadyAsync(page, ReadinessRecoveryWait, cancellation);
            }
        }
    }

    private static bool IsRecoverable(string? reason) =>
        reason != null && (reason.Contains("already contains text", StringComparison.OrdinalIgnoreCase) ||
                           reason.Contains("currently generating", StringComparison.OrdinalIgnoreCase) ||
                           reason.Contains("Composer input box is not visible", StringComparison.OrdinalIgnoreCase));

    // Comment lines keep proxies and clients from closing an idle stream while M365 is still thinking
    private static async Task KeepAliveAsync(SseChatStream stream, CancellationToken cancellationToken)
    {
        try
        {
            await stream.StartAsync();
            while (true)
            {
                await Task.Delay(KeepAliveInterval, cancellationToken);
                await stream.WriteKeepAliveAsync();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
