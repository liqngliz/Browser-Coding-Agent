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
    private readonly SemaphoreSlim _gate = new(1, 1);

    public RequestCoordinator(
        IBrowserSession browserSession,
        M365ChatAdapter adapter,
        IOptions<ChatOptions> chatOptions,
        ILogger<RequestCoordinator> logger)
    {
        _browserSession = browserSession;
        _adapter = adapter;
        _chatOptions = chatOptions.Value;
        _logger = logger;
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
        // 1. Strict input validation before any browser interaction
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

        if (request.Stream.HasValue && request.Stream.Value)
        {
            return Results.Json(
                new ErrorResponse(new ErrorDetail(
                    "Streaming is not supported in the initial release. Set stream: false.",
                    "invalid_request_error",
                    "stream",
                    "unsupported_parameter")),
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

        if (request.Tools != null || request.Functions != null || request.ResponseFormat != null)
        {
            return Results.Json(
                new ErrorResponse(new ErrorDetail(
                    "Tools, functions, and structured output formatting are not supported in this release.",
                    "invalid_request_error",
                    null,
                    "unsupported_feature")),
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (request.AdditionalProperties != null && request.AdditionalProperties.Count > 0)
        {
            var firstUnsupported = request.AdditionalProperties.Keys.First();
            return Results.Json(
                new ErrorResponse(new ErrorDetail(
                    $"Unsupported parameter '{firstUnsupported}'. Unsupported generation controls are rejected.",
                    "invalid_request_error",
                    firstUnsupported,
                    "unsupported_parameter")),
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (request.Messages == null || request.Messages.Count != 1)
        {
            return Results.Json(
                new ErrorResponse(new ErrorDetail(
                    "Only a single new user message per request is supported. Conversation history is maintained in the active browser tab.",
                    "invalid_request_error",
                    "messages",
                    "invalid_messages")),
                statusCode: StatusCodes.Status400BadRequest);
        }

        var message = request.Messages[0];
        if (message == null)
        {
            return Results.Json(
                new ErrorResponse(new ErrorDetail(
                    "Message entry cannot be null.",
                    "invalid_request_error",
                    "messages[0]",
                    "invalid_messages")),
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (!string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase))
        {
            return Results.Json(
                new ErrorResponse(new ErrorDetail(
                    $"Expected a user message, but found role '{message.Role}'.",
                    "invalid_request_error",
                    "messages[0].role",
                    "invalid_role")),
                statusCode: StatusCodes.Status400BadRequest);
        }

        var textContent = message.ExtractTextContent(out var contentError);
        if (contentError != null)
        {
            return Results.Json(
                new ErrorResponse(new ErrorDetail(
                    contentError,
                    "invalid_request_error",
                    "messages[0].content",
                    "invalid_content")),
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (string.IsNullOrWhiteSpace(textContent))
        {
            return Results.Json(
                new ErrorResponse(new ErrorDetail(
                    "Message content cannot be empty.",
                    "invalid_request_error",
                    "messages[0].content",
                    "empty_content")),
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

            CurrentState = ChatTurnState.Submitting;

            try
            {
                var answer = await _adapter.SendAndObserveAsync(
                    page,
                    textContent,
                    timeout,
                    httpContext.RequestAborted,
                    onSendDispatched: () =>
                    {
                        CurrentState = ChatTurnState.Generating;
                    });

                CurrentState = ChatTurnState.Idle;
                RecoveryReason = null;

                var response = new ChatCompletionResponse(
                    Id: $"chatcmpl-{Guid.NewGuid():N}",
                    Object: "chat.completion",
                    Created: DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Model: _chatOptions.ModelAlias,
                    Choices: new List<ChatChoice>
                    {
                        new(
                            Index: 0,
                            Message: new ChatMessage("assistant", answer),
                            FinishReason: "stop")
                    });

                return Results.Ok(response);
            }
            catch (OperationCanceledException) when (httpContext.RequestAborted.IsCancellationRequested && CurrentState == ChatTurnState.Submitting)
            {
                CurrentState = ChatTurnState.Idle;
                return Results.Json(
                    new ErrorResponse(new ErrorDetail(
                        "Client cancelled request before submission.",
                        "invalid_request_error",
                        null,
                        "request_cancelled")),
                    statusCode: StatusCodes.Status400BadRequest);
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
                return Results.Json(
                    new ErrorResponse(new ErrorDetail(
                        tex.Message,
                        "timeout_error",
                        null,
                        "deadline_exceeded")),
                    statusCode: StatusCodes.Status504GatewayTimeout);
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
                return Results.Json(
                    new ErrorResponse(new ErrorDetail(
                        $"Upstream error from Copilot UI: {uex.Message}",
                        "api_error",
                        null,
                        "upstream_error")),
                    statusCode: StatusCodes.Status502BadGateway);
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
                return Results.Json(
                    new ErrorResponse(new ErrorDetail(
                        iex.Message,
                        "conflict_error",
                        null,
                        "invalid_operation")),
                    statusCode: StatusCodes.Status409Conflict);
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
                return Results.Json(
                    new ErrorResponse(new ErrorDetail(
                        ex.Message,
                        "api_error",
                        null,
                        "internal_error")),
                    statusCode: StatusCodes.Status500InternalServerError);
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}
