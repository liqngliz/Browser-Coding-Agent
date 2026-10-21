using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BrowserCodingAgent.Api.Browser;
using BrowserCodingAgent.Api.Contracts;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace BrowserCodingAgent.Tests;

// Same persistent profile as M365IntegrationTests: one collection so they never run concurrently
[CollectionDefinition("M365Live", DisableParallelization = true)]
public class M365LiveCollection { }

[Collection("M365Live")]
public class M365ChainLiveTests
{
    private const bool SkipLocal = false;

    private readonly ITestOutputHelper _output;

    public M365ChainLiveTests(ITestOutputHelper output) => _output = output;

    // Client-style history (full conversation resent each call, system prompt included every time).
    // The server forwards the system prompt only on turn 1; turns 2-3 check M365 still follows it.
    [Fact(Skip = SkipLocal ? "Local M365 integration test skipped." : null)]
    [Trait("Category", "ManualLiveValidation")]
    public async Task LocalValidation_MultiTurnChain_KeepsSystemRulesAndContext()
    {
        var profile = Environment.GetEnvironmentVariable("M365_TEST_PROFILE") ??
                      Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                          ".browser-coding-agent", "profiles", "m365-live-test");

        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Browser:Channel", Environment.GetEnvironmentVariable("M365_TEST_CHANNEL") ?? "chrome");
            builder.UseSetting("Browser:Headless", "false");
            builder.UseSetting("Browser:AutoStart", "true");
            builder.UseSetting("Browser:UserDataDirectory", profile);
            builder.UseSetting("Chat:ResponseTimeoutMinutes", "3");
        });
        var client = factory.CreateClient();
        client.Timeout = TimeSpan.FromMinutes(5);

        var sw = Stopwatch.StartNew();
        HttpResponseMessage ready;
        do
        {
            ready = await client.GetAsync("/health/ready");
            if (ready.StatusCode == HttpStatusCode.OK) break;
            await Task.Delay(1000);
        } while (sw.Elapsed < TimeSpan.FromSeconds(120));
        Assert.True(ready.StatusCode == HttpStatusCode.OK, $"Not ready: {await ready.Content.ReadAsStringAsync()}");

        var session = factory.Services.GetRequiredService<IBrowserSession>();
        var id = Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        var tag = $"[CHAINBOT-{id}]";
        var word1 = $"KESTREL{id}";
        var word2 = $"MARLIN{id}";

        var history = new List<ChatMessageRequest?>
        {
            new("system",
                $"You are CHAINBOT.\nRules for every reply in this conversation:\n" +
                $"1. Start the reply with the exact tag {tag}\n" +
                "2. Keep the reply under 40 words.\n" +
                "3. Remember every codeword the user gives you."),
            new("user", $"My first codeword is {word1}. Acknowledge it.")
        };

        // Turn 1: non-streamed; starts a new chat with system prompt + message
        var reply1 = await CompleteAsync(client, history, stream: false);

        // M365 switches /chat -> /chat/conversation/{id} shortly after the first reply completes
        var urlWait = Stopwatch.StartNew();
        while (!session.ActivePage!.Url.Contains("/conversation/") && urlWait.Elapsed < TimeSpan.FromSeconds(15))
            await Task.Delay(250);
        var conversationUrl = session.ActivePage!.Url;
        _output.WriteLine($"turn 1 ({conversationUrl}):\n{reply1}\n");
        Assert.Contains("/conversation/", conversationUrl);
        Assert.Contains(tag, reply1);

        // Turn 2: streamed; only the new user message reaches M365
        history.Add(new("assistant", reply1));
        history.Add(new("user", "What is my first codeword? Reply with the tag and the codeword only."));
        var reply2 = await CompleteAsync(client, history, stream: true);
        _output.WriteLine($"turn 2:\n{reply2}\n");
        Assert.Equal(conversationUrl, session.ActivePage!.Url);
        Assert.Contains(tag, reply2);
        Assert.Contains(word1, reply2);

        // Turn 3: streamed; context from turns 1 and 3 together
        history.Add(new("assistant", reply2));
        history.Add(new("user", $"My second codeword is {word2}. List both codewords."));
        var reply3 = await CompleteAsync(client, history, stream: true);
        _output.WriteLine($"turn 3:\n{reply3}\n");
        Assert.Equal(conversationUrl, session.ActivePage!.Url);
        Assert.Contains(tag, reply3);
        Assert.Contains(word1, reply3);
        Assert.Contains(word2, reply3);
    }

    private static async Task<string> CompleteAsync(HttpClient client, List<ChatMessageRequest?> messages, bool stream)
    {
        var response = await client.PostAsJsonAsync("/v1/chat/completions",
            new ChatCompletionRequest("m365-active", messages, stream: stream));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"HTTP {(int)response.StatusCode}: {body}");

        if (!stream)
            return JsonDocument.Parse(body).RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()!;

        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        var data = body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Where(e => e.StartsWith("data: ", StringComparison.Ordinal))
            .Select(e => e["data: ".Length..])
            .ToList();
        Assert.Equal("[DONE]", data[^1]);

        return string.Concat(data.SkipLast(1)
            .Select(d => JsonDocument.Parse(d).RootElement)
            .Where(c => c.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
            .Select(c => c.GetProperty("choices")[0].GetProperty("delta"))
            .Where(delta => delta.TryGetProperty("content", out _))
            .Select(delta => delta.GetProperty("content").GetString()));
    }
}
