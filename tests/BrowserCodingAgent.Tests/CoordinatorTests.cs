using BrowserCodingAgent.Api.Browser;
using BrowserCodingAgent.Api.Configuration;
using BrowserCodingAgent.Api.Contracts;
using BrowserCodingAgent.Api.Providers.M365;
using BrowserCodingAgent.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;

namespace BrowserCodingAgent.Tests;

public class CoordinatorTests
{
    private class FakeBrowserSession : IBrowserSession
    {
        public bool IsRunning { get; set; } = true;
        public string? LastError { get; set; }
        public IPage? ActivePage { get; set; }
        public bool ShouldBeReady { get; set; } = true;
        public string? NotReadyReason { get; set; }

        public Task<(bool Ready, string? Reason)> CheckReadyAsync(M365ChatAdapter adapter) =>
            Task.FromResult((ShouldBeReady, NotReadyReason));

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task ConcurrencyGate_RejectsSimultaneousRequestsWith409()
    {
        var session = new FakeBrowserSession();
        var options = Options.Create(new ChatOptions
        {
            ModelAlias = "m365-active",
            ResponseTimeoutMinutes = 1
        });
        var adapter = new M365ChatAdapter(options, NullLogger<M365ChatAdapter>.Instance);
        var coordinator = new RequestCoordinator(session, adapter, options, NullLogger<RequestCoordinator>.Instance);

        var firstRequestStarted = new TaskCompletionSource<bool>();
        var allowFirstRequestToFinish = new TaskCompletionSource<bool>();

        // Make session not ready after waiting to simulate an in-progress request
        session.ShouldBeReady = true;

        // Run first request on background thread that holds the gate
        var req1 = new ChatCompletionRequest("m365-active", new() { new("user", "prompt 1") });
        var req2 = new ChatCompletionRequest("m365-active", new() { new("user", "prompt 2") });

        // Pre-cancel request 1 after gate acquisition by passing a cancellation token that cancels after start
        var cts = new CancellationTokenSource();
        var httpContext1 = new DefaultHttpContext();
        httpContext1.RequestAborted = cts.Token;

        // Cancel right away so req1 checks readiness, sees cancelled before submission, and exits
        cts.Cancel();

        // While gate is held or tested concurrently:
        // Acquire gate manually to test coordinator concurrency response directly
        var gateField = typeof(RequestCoordinator).GetField("_gate", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var gate = (SemaphoreSlim)gateField!.GetValue(coordinator)!;
        await gate.WaitAsync();

        try
        {
            var res2 = await coordinator.ExecuteChatCompletionAsync(req2, new DefaultHttpContext());
            var jsonResult = Assert.IsType<JsonHttpResult<ErrorResponse>>(res2);
            Assert.Equal(StatusCodes.Status409Conflict, jsonResult.StatusCode);
            Assert.Equal("concurrent_request", jsonResult.Value?.Error.Code);
        }
        finally
        {
            gate.Release();
        }
    }

    [Fact]
    public async Task WhenBrowserNotReady_Returns503WithReason()
    {
        var session = new FakeBrowserSession
        {
            ShouldBeReady = false,
            NotReadyReason = "Sign-in required in browser window."
        };
        var options = Options.Create(new ChatOptions { ModelAlias = "m365-active" });
        var adapter = new M365ChatAdapter(options, NullLogger<M365ChatAdapter>.Instance);
        var coordinator = new RequestCoordinator(session, adapter, options, NullLogger<RequestCoordinator>.Instance);

        var request = new ChatCompletionRequest("m365-active", new() { new("user", "Hello") });
        var httpContext = new DefaultHttpContext();
        var result = await coordinator.ExecuteChatCompletionAsync(request, httpContext);

        var jsonResult = Assert.IsType<JsonHttpResult<ErrorResponse>>(result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, jsonResult.StatusCode);
        Assert.Equal("browser_not_ready", jsonResult.Value?.Error.Code);
        Assert.Contains("Sign-in required", jsonResult.Value?.Error.Message);
    }

    [Fact]
    public async Task WhenClientCancelledBeforeSubmission_Returns400Cancelled()
    {
        var session = new FakeBrowserSession { ShouldBeReady = true };
        var options = Options.Create(new ChatOptions { ModelAlias = "m365-active" });
        var adapter = new M365ChatAdapter(options, NullLogger<M365ChatAdapter>.Instance);
        var coordinator = new RequestCoordinator(session, adapter, options, NullLogger<RequestCoordinator>.Instance);

        var request = new ChatCompletionRequest("m365-active", new() { new("user", "Hello") });
        var httpContext = new DefaultHttpContext();
        var cts = new CancellationTokenSource();
        cts.Cancel();
        httpContext.RequestAborted = cts.Token;

        var result = await coordinator.ExecuteChatCompletionAsync(request, httpContext);
        var jsonResult = Assert.IsType<JsonHttpResult<ErrorResponse>>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, jsonResult.StatusCode);
        Assert.Equal("request_cancelled", jsonResult.Value?.Error.Code);
        Assert.Equal(ChatTurnState.Idle, coordinator.CurrentState);
    }

    [Fact]
    public async Task WhenPreSubmissionFails_DoesNotLockIntoRecoveryState()
    {
        var session = new FakeBrowserSession { ShouldBeReady = true };
        var options = Options.Create(new ChatOptions { ModelAlias = "m365-active" });
        var adapter = new M365ChatAdapter(options, NullLogger<M365ChatAdapter>.Instance);
        var coordinator = new RequestCoordinator(session, adapter, options, NullLogger<RequestCoordinator>.Instance);

        // Client cancellation before submission
        var request = new ChatCompletionRequest("m365-active", new() { new("user", "Hello") });
        var httpContext = new DefaultHttpContext();
        var cts = new CancellationTokenSource();
        cts.Cancel();
        httpContext.RequestAborted = cts.Token;

        await coordinator.ExecuteChatCompletionAsync(request, httpContext);
        // Coordinator must remain in Idle state because Send was never clicked
        Assert.Equal(ChatTurnState.Idle, coordinator.CurrentState);

        // A second valid request is NOT blocked by recovery state
        var req2 = new ChatCompletionRequest("m365-active", new() { new("user", "Hello again") });
        session.ShouldBeReady = false;
        session.NotReadyReason = "Test not ready";
        var res2 = await coordinator.ExecuteChatCompletionAsync(req2, new DefaultHttpContext());
        var jsonResult = Assert.IsType<JsonHttpResult<ErrorResponse>>(res2);
        // Returns browser_not_ready (503), NOT manual_recovery_required!
        Assert.Equal("browser_not_ready", jsonResult.Value?.Error.Code);
    }

    [Fact]
    public async Task GuardedRecovery_ClearsUncertainState_WhenBrowserBecomesReady()
    {
        var session = new FakeBrowserSession { ShouldBeReady = true };
        var options = Options.Create(new ChatOptions { ModelAlias = "m365-active" });
        var adapter = new M365ChatAdapter(options, NullLogger<M365ChatAdapter>.Instance);
        var coordinator = new RequestCoordinator(session, adapter, options, NullLogger<RequestCoordinator>.Instance);

        // Simulate recovery state
        var stateProp = typeof(RequestCoordinator).GetProperty("CurrentState");
        stateProp!.SetValue(coordinator, ChatTurnState.UncertainRecoveryRequired);

        // Browser is confirmed idle and ready
        session.ShouldBeReady = true;

        var (ready, reason) = await coordinator.CheckCoordinatorReadyAsync();
        Assert.True(ready);
        Assert.Null(reason);
        Assert.Equal(ChatTurnState.Idle, coordinator.CurrentState);
    }
}
