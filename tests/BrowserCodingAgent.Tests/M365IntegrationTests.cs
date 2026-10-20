using System.Diagnostics;
using BrowserCodingAgent.Api.Browser;
using BrowserCodingAgent.Api.Configuration;
using BrowserCodingAgent.Api.Providers.M365;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BrowserCodingAgent.Tests;

public class M365IntegrationTests
{
    private const bool SkipLocal = false;

    private class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "BrowserCodingAgent.Tests";
        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    [Fact(Skip = SkipLocal ? "Local M365 integration test skipped." : null)]
    [Trait("Category", "ManualLiveValidation")]
    public async Task LocalValidation_AgainstM365Url_SendsPromptAndVerifiesUniqueToken()
    {
        // 1. Open visible Chrome/Edge with a dedicated persistent profile
        var channel = Environment.GetEnvironmentVariable("M365_TEST_CHANNEL") ?? "chrome";
        var startUrl = Environment.GetEnvironmentVariable("M365_START_URL") ?? "https://m365.cloud.microsoft/chat";
        var persistentProfile = Environment.GetEnvironmentVariable("M365_TEST_PROFILE") ??
                                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                                    ".browser-coding-agent", "profiles", "m365-live-test");

        Directory.CreateDirectory(persistentProfile);

        var timeoutMinutes = 2;
        if (int.TryParse(Environment.GetEnvironmentVariable("M365_TIMEOUT_MINUTES"), out var parsedTimeout) && parsedTimeout > 0)
        {
            timeoutMinutes = parsedTimeout;
        }

        var browserOptions = Options.Create(new BrowserOptions
        {
            Channel = channel,
            Headless = false, // Standalone visible browser window
            StartUrl = startUrl,
            UserDataDirectory = persistentProfile
        });

        var chatOptions = Options.Create(new ChatOptions
        {
            ModelAlias = "m365-active",
            ResponseTimeoutMinutes = timeoutMinutes
        });

        var env = new TestHostEnvironment();
        var adapter = new M365ChatAdapter(chatOptions, NullLogger<M365ChatAdapter>.Instance);
        await using var session = new BrowserSession(browserOptions, env, NullLogger<BrowserSession>.Instance);

        await session.StartAsync();
        Assert.NotNull(session.ActivePage);

        // 2. Allow manual sign-in and model/chat selection, then require readiness
        var setupTimeoutSeconds = 120;
        if (int.TryParse(Environment.GetEnvironmentVariable("M365_SETUP_TIMEOUT_SECONDS"), out var parsedSetup) && parsedSetup > 0)
        {
            setupTimeoutSeconds = parsedSetup;
        }

        var sw = Stopwatch.StartNew();
        var isReady = false;
        string? notReadyReason = null;

        while (sw.Elapsed < TimeSpan.FromSeconds(setupTimeoutSeconds))
        {
            (isReady, notReadyReason) = await session.CheckReadyAsync(adapter);
            if (isReady)
                break;

            await Task.Delay(1000);
        }

        Assert.True(isReady, $"Browser was not ready within {setupTimeoutSeconds}s setup window: {notReadyReason}");

        // 3. Send a prompt requesting a unique response token
        var expectedToken = "M365_VERIFY_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var prompt = $"Please reply with the exact token {expectedToken} and no other text.";

        // 4. Await SendAndObserveAsync using ResponseTimeoutMinutes
        var sendDispatched = false;
        var responseTimeout = TimeSpan.FromMinutes(chatOptions.Value.ResponseTimeoutMinutes);

        var answer = await adapter.SendAndObserveAsync(
            session.ActivePage,
            prompt,
            responseTimeout,
            CancellationToken.None,
            onSendDispatched: () =>
            {
                sendDispatched = true;
            });

        // 5. Assert Send was dispatched and returned answer matches the unique expected token
        Assert.True(sendDispatched, "Send button click was never dispatched to the browser UI.");
        Assert.False(string.IsNullOrWhiteSpace(answer), "Returned assistant answer was empty.");
        Assert.Contains(expectedToken, answer, StringComparison.OrdinalIgnoreCase);
    }
}
