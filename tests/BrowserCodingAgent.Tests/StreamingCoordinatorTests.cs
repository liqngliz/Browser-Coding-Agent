using System.Text;
using System.Text.Json;
using BrowserCodingAgent.Api.Browser;
using BrowserCodingAgent.Api.Configuration;
using BrowserCodingAgent.Api.Contracts;
using BrowserCodingAgent.Api.Providers.M365;
using BrowserCodingAgent.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;

namespace BrowserCodingAgent.Tests;

public class StreamingCoordinatorTests : IAsyncLifetime
{
    private IPlaywright? _playwright;
    private IBrowser? _browser;

    // Records the submitted text and new-chat clicks; the reply grows one word every 200ms
    private const string FakeChatPage = @"
<!DOCTYPE html>
<html>
<body>
  <button aria-label='New chat'>New chat</button>
  <div id='log'></div>
  <textarea placeholder='Ask Microsoft 365 Copilot'></textarea>
  <button aria-label='Send'>Send</button>
  <button aria-label='Stop generating' style='display:none'>Stop</button>
  <script>
    window.sent = [];
    window.newChatClicks = 0;
    const composer = document.querySelector('textarea');
    const stop = document.querySelector(""button[aria-label='Stop generating']"");
    document.querySelector(""button[aria-label='New chat']"").addEventListener('click', () => {
      window.newChatClicks++;
      document.getElementById('log').innerHTML = '';
    });
    document.querySelector(""button[aria-label='Send']"").addEventListener('click', () => {
      const text = composer.value;
      if (!text.trim()) return;
      window.sent.push(text);
      composer.value = '';
      stop.style.display = 'inline-block';
      const reply = document.createElement('div');
      reply.setAttribute('data-message-author', 'assistant');
      const p = document.createElement('p');
      reply.append(p);
      document.getElementById('log').append(reply);
      const words = ['Alpha', 'beta', 'gamma', 'delta'];
      let i = 0;
      const timer = setInterval(() => {
        p.textContent = words.slice(0, ++i).join(' ');
        if (i === words.length) { clearInterval(timer); stop.style.display = 'none'; }
      }, 200);
    });
  </script>
</body>
</html>";

    private class PageBrowserSession : IBrowserSession
    {
        public PageBrowserSession(IPage page) => ActivePage = page;
        public IPage? ActivePage { get; }
        public bool IsRunning => true;
        public string? LastError => null;
        public Task<(bool Ready, string? Reason)> CheckReadyAsync(M365ChatAdapter adapter) => adapter.CheckPageReadyAsync(ActivePage!);
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    public async Task InitializeAsync()
    {
        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
    }

    public async Task DisposeAsync()
    {
        if (_browser != null)
            await _browser.DisposeAsync();
        _playwright?.Dispose();
    }

    private async Task<(RequestCoordinator Coordinator, IPage Page)> CreateAsync()
    {
        var page = await _browser!.NewPageAsync();
        await page.SetContentAsync(FakeChatPage);
        var options = Options.Create(new ChatOptions { ModelAlias = "m365-active", ResponseTimeoutMinutes = 1 });
        var adapter = new M365ChatAdapter(options, NullLogger<M365ChatAdapter>.Instance);
        var coordinator = new RequestCoordinator(new PageBrowserSession(page), adapter, options, NullLogger<RequestCoordinator>.Instance);
        return (coordinator, page);
    }

    private static List<string> ReadSseData(DefaultHttpContext context)
    {
        var body = Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());
        return body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Where(e => e.StartsWith("data: ", StringComparison.Ordinal))
            .Select(e => e["data: ".Length..])
            .ToList();
    }

    [Fact]
    public async Task Stream_FirstTurn_StartsNewChat_BundlesSystemPrompt_AndStreamsChunks()
    {
        var (coordinator, page) = await CreateAsync();
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        var request = new ChatCompletionRequest(
            "m365-active",
            new List<ChatMessageRequest?> { new("system", "Be terse."), new("user", "Hello") },
            stream: true)
        {
            StreamOptions = new StreamOptionsRequest { IncludeUsage = true }
        };

        await coordinator.ExecuteChatCompletionAsync(request, context);

        Assert.Equal("text/event-stream", context.Response.ContentType);
        Assert.Equal(1, await page.EvaluateAsync<int>("window.newChatClicks"));
        Assert.Equal("Be terse.\n\nHello", await page.EvaluateAsync<string>("window.sent[0]"));

        var data = ReadSseData(context);
        Assert.Equal("[DONE]", data[^1]);

        var chunks = data.SkipLast(1).Select(d => JsonDocument.Parse(d).RootElement).ToList();
        Assert.Equal("assistant", chunks[0].GetProperty("choices")[0].GetProperty("delta").GetProperty("role").GetString());
        Assert.All(chunks, c => Assert.Equal("chat.completion.chunk", c.GetProperty("object").GetString()));

        var contentChunks = chunks
            .Where(c => c.GetProperty("choices").GetArrayLength() > 0 &&
                        c.GetProperty("choices")[0].GetProperty("delta").TryGetProperty("content", out _))
            .Select(c => c.GetProperty("choices")[0].GetProperty("delta").GetProperty("content").GetString())
            .ToList();
        Assert.Equal("Alpha beta gamma delta", string.Concat(contentChunks));
        Assert.True(contentChunks.Count(c => !string.IsNullOrEmpty(c)) > 1, "Expected the answer to arrive in more than one delta.");

        var finish = chunks.Single(c => c.GetProperty("choices").GetArrayLength() > 0 &&
                                        c.GetProperty("choices")[0].GetProperty("finish_reason").ValueKind == JsonValueKind.String);
        Assert.Equal("stop", finish.GetProperty("choices")[0].GetProperty("finish_reason").GetString());

        var usage = chunks.Single(c => c.TryGetProperty("usage", out _));
        Assert.Equal(0, usage.GetProperty("choices").GetArrayLength());
        Assert.Equal(ChatTurnState.Idle, coordinator.CurrentState);
    }

    // Renders the reply like M365: a paragraph plus a <pre><code class='language-tool_call'> block, using the alias name
    private const string FakeToolPage = @"
<!DOCTYPE html>
<html>
<body>
  <button aria-label='New chat'>New chat</button>
  <div id='log'></div>
  <textarea placeholder='Ask Microsoft 365 Copilot'></textarea>
  <button aria-label='Send'>Send</button>
  <button aria-label='Stop generating' style='display:none'>Stop</button>
  <script>
    window.sent = [];
    const composer = document.querySelector('textarea');
    const stop = document.querySelector(""button[aria-label='Stop generating']"");
    document.querySelector(""button[aria-label='Send']"").addEventListener('click', () => {
      window.sent.push(composer.value);
      composer.value = '';
      stop.style.display = 'inline-block';
      setTimeout(() => {
        const reply = document.createElement('div');
        reply.setAttribute('data-message-author', 'assistant');
        reply.innerHTML = window.replyHtml;
        document.getElementById('log').append(reply);
        stop.style.display = 'none';
      }, 400);
    });
  </script>
</body>
</html>";

    private static ChatCompletionRequest ToolRequest(bool stream) => new(
        "m365-active",
        new List<ChatMessageRequest?> { new("system", "You are Cline, an AI coding agent."), new("user", "Read /a.cs") },
        stream: stream,
        tools: JsonDocument.Parse("""[{"type":"function","function":{"name":"read_files","description":"Read files.","parameters":{"type":"object"}}}]""").RootElement)
    {
        ToolChoice = JsonDocument.Parse("\"auto\"").RootElement
    };

    // Reply as a <pre><code class='language-tool_call'> block (generic markdown renderer)
    private const string PreCodeReply =
        "<p>Reading the file.</p><pre><code class='language-tool_call'>{\"name\": \"open_documents\", \"arguments\": {\"files\": [\"/a.cs\"]}}</code></pre>";

    // M365's real code-block markup (Fluent components, no <pre>), captured from the live page
    private const string M365CodeBlockReply =
        "<p>Reading the file.</p>" +
        "<div role='group' aria-label='Code Preview'><div class='scriptor-component-code-block'>" +
        "<div data-testid='one-copilot-code-identity'><span>Shell</span></div>" +
        "<button aria-label='Copy code'></button>" +
        "<div class='fui-MessageBar'><div data-testid='message-bar-body-info'>tool_call isn't fully supported. Syntax highlighting is based on Shell.</div></div>" +
        "<div role='textbox' aria-label='Code editor'><div>{\"name\": \"open_documents\", \"arguments\": {\"files\": [\"/a.cs\"]}}</div></div>" +
        "</div></div>";

    private async Task<(RequestCoordinator Coordinator, IPage Page)> CreateToolAsync(string replyHtml = PreCodeReply)
    {
        var page = await _browser!.NewPageAsync();
        await page.SetContentAsync(FakeToolPage);
        await page.EvaluateAsync("html => { window.replyHtml = html; }", replyHtml);
        var options = Options.Create(new ChatOptions { ModelAlias = "m365-active", ResponseTimeoutMinutes = 1 });
        var adapter = new M365ChatAdapter(options, NullLogger<M365ChatAdapter>.Instance);
        var replacements = new PromptReplacements(new[]
        {
            new ReplacementRule("Cline", "Codewright", Reverse: true),
            new ReplacementRule("read_files", "open_documents", Reverse: true)
        }, "test");
        var coordinator = new RequestCoordinator(new PageBrowserSession(page), adapter, options, NullLogger<RequestCoordinator>.Instance, replacements);
        return (coordinator, page);
    }

    [Fact]
    public async Task ToolCall_NonStreamed_ReturnsToolCallsWithOriginalName()
    {
        var (coordinator, page) = await CreateToolAsync();
        var context = new DefaultHttpContext();

        var result = await coordinator.ExecuteChatCompletionAsync(ToolRequest(stream: false), context);

        var sent = await page.EvaluateAsync<string>("window.sent[0]");
        Assert.Contains("You are Codewright, an AI coding agent.", sent);
        Assert.Contains("- open_documents: Read files.", sent);
        Assert.Contains("```tool_call", sent);
        Assert.DoesNotContain("read_files", sent);

        var ok = Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.Ok<ChatCompletionResponse>>(result);
        var choice = ok.Value!.Choices[0];
        Assert.Equal("tool_calls", choice.FinishReason);
        Assert.Equal("Reading the file.", choice.Message.Content);
        var call = Assert.Single(choice.Message.ToolCalls!);
        Assert.Equal("read_files", call.Function.Name);
        Assert.Equal("{\"files\":[\"/a.cs\"]}", call.Function.Arguments);
    }

    [Fact]
    public async Task ToolCall_Streamed_SendsTextThenToolCallDelta_WithoutLeakingTheBlock()
    {
        var (coordinator, _) = await CreateToolAsync();
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        await coordinator.ExecuteChatCompletionAsync(ToolRequest(stream: true), context);

        var data = ReadSseData(context);
        Assert.Equal("[DONE]", data[^1]);
        var deltas = data.SkipLast(1).Select(d => JsonDocument.Parse(d).RootElement)
            .Select(c => c.GetProperty("choices")[0]).ToList();

        var text = string.Concat(deltas
            .Where(c => c.GetProperty("delta").TryGetProperty("content", out _))
            .Select(c => c.GetProperty("delta").GetProperty("content").GetString()));
        Assert.Equal("Reading the file.", text.Trim());
        Assert.DoesNotContain("```", text);

        var toolDelta = deltas.Single(c => c.GetProperty("delta").TryGetProperty("tool_calls", out _))
            .GetProperty("delta").GetProperty("tool_calls")[0];
        Assert.Equal(0, toolDelta.GetProperty("index").GetInt32());
        Assert.Equal("read_files", toolDelta.GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("{\"files\":[\"/a.cs\"]}", toolDelta.GetProperty("function").GetProperty("arguments").GetString());
        Assert.Equal("tool_calls", deltas.Single(c => c.GetProperty("finish_reason").ValueKind == JsonValueKind.String)
            .GetProperty("finish_reason").GetString());
    }

    [Fact]
    public async Task ToolCall_InM365CodeBlockMarkup_IsParsed_WithoutCodeBlockChrome()
    {
        var (coordinator, _) = await CreateToolAsync(M365CodeBlockReply);

        var result = await coordinator.ExecuteChatCompletionAsync(ToolRequest(stream: false), new DefaultHttpContext());

        var ok = Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.Ok<ChatCompletionResponse>>(result);
        var message = ok.Value!.Choices[0].Message;
        Assert.Equal("Reading the file.", message.Content);
        Assert.Equal("read_files", Assert.Single(message.ToolCalls!).Function.Name);
        Assert.Equal("{\"files\":[\"/a.cs\"]}", message.ToolCalls![0].Function.Arguments);
    }

    [Fact]
    public async Task CrlfPrompt_IsSubmittedWithLf()
    {
        // A textarea (and M365's editor) stores line breaks as \n; CRLF input used to fail the exact readback check
        var (coordinator, page) = await CreateAsync();
        var request = new ChatCompletionRequest("m365-active", new List<ChatMessageRequest?> { new("user", "line one\r\nline two") });

        var result = await coordinator.ExecuteChatCompletionAsync(request, new DefaultHttpContext());

        Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.Ok<ChatCompletionResponse>>(result);
        Assert.Equal("line one\nline two", await page.EvaluateAsync<string>("window.sent[0]"));
    }

    [Fact]
    public async Task BomAndZeroWidthInPrompt_AreStrippedBeforeSubmit()
    {
        // Visual Studio saves .cs/.csproj with a BOM; Cline's file reads carry it into the prompt
        var (coordinator, page) = await CreateAsync();
        var request = new ChatCompletionRequest("m365-active", new List<ChatMessageRequest?>
        {
            new("user", "Result of read_files:\n\uFEFF<Project Sdk=\"Microsoft.NET.Sdk\">\n</Project>\nzero\u200Bwidth")
        });

        var result = await coordinator.ExecuteChatCompletionAsync(request, new DefaultHttpContext());

        Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.Ok<ChatCompletionResponse>>(result);
        Assert.Equal("Result of read_files:\n<Project Sdk=\"Microsoft.NET.Sdk\">\n</Project>\nzerowidth",
            await page.EvaluateAsync<string>("window.sent[0]"));
    }

    [Fact]
    public async Task LaterTurn_SendsOnlyNewMessage_WithoutNewChat()
    {
        var (coordinator, page) = await CreateAsync();
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        var request = new ChatCompletionRequest(
            "m365-active",
            new List<ChatMessageRequest?>
            {
                new("system", "Be terse."),
                new("user", "First"),
                new("assistant", "Alpha beta gamma delta"),
                new("user", "Second")
            });

        var result = await coordinator.ExecuteChatCompletionAsync(request, context);

        Assert.Equal(0, await page.EvaluateAsync<int>("window.newChatClicks"));
        Assert.Equal("Second", await page.EvaluateAsync<string>("window.sent[0]"));
        var ok = Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.Ok<ChatCompletionResponse>>(result);
        Assert.Equal("Alpha beta gamma delta", ok.Value!.Choices[0].Message.Content);
    }
}
