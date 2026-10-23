using BrowserCodingAgent.Api.Configuration;
using BrowserCodingAgent.Api.Providers;
using BrowserCodingAgent.Api.Providers.M365;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;

namespace BrowserCodingAgent.Tests;

// Fake gemini.google.com/app built from the live page's markup: Quill composer (one <p> per line), model-response >
// message-content replies with ids, .response-footer.complete when done, code-block components, /app -> /app/{id}.
public class GeminiSimulatedTests : IAsyncLifetime
{
    private IPlaywright? _playwright;
    private IBrowser? _browser;

    private const string PageHtml = @"<!DOCTYPE html>
<html>
<body>
  <a aria-label='New chat' href='/app'>New chat</a>
  <div id='chat'></div>
  <rich-textarea><div class='ql-editor ql-blank' contenteditable='true' role='textbox' aria-label='Enter a prompt for Gemini'><p><br></p></div></rich-textarea>
  <div class='ql-clipboard' contenteditable='true'></div>
  <button aria-label='Send message' disabled>Send</button>
  <button aria-label='Stop response' style='display:none'>Stop</button>
  <script>
    const editor = document.querySelector('.ql-editor');
    const send = document.querySelector(""button[aria-label='Send message']"");
    const stop = document.querySelector(""button[aria-label='Stop response']"");
    const chat = document.getElementById('chat');
    let text = '';
    let turns = 0;
    window.sentPrompts = [];
    window.replyHtml = p => '<p>Echo: ' + p.split('\n')[0] + '</p>';

    // Quill keeps a text model and renders one <p> per line
    const render = () => {
      editor.innerHTML = text.split('\n').map(l => l ? '<p>' + l.replace(/&/g, '&amp;').replace(/</g, '&lt;') + '</p>' : '<p><br></p>').join('');
      editor.classList.toggle('ql-blank', text === '');
      send.disabled = text.trim() === '';
    };
    editor.addEventListener('beforeinput', e => {
      e.preventDefault();
      const all = !window.getSelection().isCollapsed || text === '';
      if (e.inputType.startsWith('insert')) text = all ? (e.data || '') : text + (e.data || '');
      else if (e.inputType.startsWith('delete')) text = all ? '' : text.slice(0, -1);
      render();
    });
    editor.addEventListener('keydown', e => {
      if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); submit(); }
    });
    send.addEventListener('click', () => submit());

    function submit() {
      if (text.trim() === '') return;
      const prompt = text;
      window.sentPrompts.push(prompt);
      text = ''; render();
      turns++;
      if (location.pathname === '/app') history.pushState({}, '', '/app/c0ffee' + turns);
      const q = document.createElement('user-query');
      q.textContent = prompt;
      chat.appendChild(q);
      send.style.display = 'none';
      stop.style.display = 'inline-block';
      const r = document.createElement('model-response');
      r.innerHTML = '<h2 class=""cdk-visually-hidden"">Gemini said</h2><message-content id=""message-content-id-r_' + turns + '""></message-content>';
      setTimeout(() => {
        chat.appendChild(r);
        r.querySelector('message-content').innerHTML = window.replyHtml(prompt);
      }, 200);
      setTimeout(() => {
        const footer = document.createElement('div');
        footer.className = 'response-footer complete';
        r.appendChild(footer);
        stop.style.display = 'none';
        send.style.display = 'inline-block';
      }, 700);
    }
  </script>
</body>
</html>";

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

    private static M365ChatAdapter CreateAdapter() =>
        new(Options.Create(new ChatOptions { Provider = "gemini", ResponseTimeoutMinutes = 1 }), NullLogger<M365ChatAdapter>.Instance);

    private async Task<IPage> OpenFakeGeminiAsync(string html = PageHtml)
    {
        var page = await _browser!.NewPageAsync();
        await page.RouteAsync("https://gemini.google.com/**", route =>
            route.FulfillAsync(new RouteFulfillOptions { ContentType = "text/html", Body = html }));
        await page.GotoAsync("https://gemini.google.com/app");
        return page;
    }

    [Fact]
    public void Provider_Gemini_DefaultsAliasAndStartUrl()
    {
        var chat = new ChatOptions { Provider = "gemini" };
        Assert.Equal("gemini-active", chat.ModelAlias);
        Assert.Equal("m365-active", new ChatOptions().ModelAlias);
        Assert.Equal("https://gemini.google.com/app", new BrowserOptions().ResolveStartUrl(ChatSite.Get("gemini")));
        Assert.EndsWith(Path.Combine("profiles", "gemini-msedge"),
            new BrowserOptions { Channel = "msedge" }.ResolveUserDataDirectory("/tmp", ChatSite.Get("Gemini")));
        Assert.EndsWith(Path.Combine("profiles", "chrome"), new BrowserOptions().ResolveUserDataDirectory("/tmp", ChatSite.M365));
        Assert.Throws<InvalidOperationException>(() => new ChatOptions { Provider = "bard" }.Validate());
    }

    [Fact]
    public async Task FirstTurn_MultiLinePrompt_ReadBackExactly_AndUrlChangeAccepted()
    {
        var page = await OpenFakeGeminiAsync();
        var adapter = CreateAdapter();
        var prompt = "Line one\n\nLine three after a blank line\n  indented";

        var ready = await adapter.CheckPageReadyAsync(page);
        Assert.True(ready.Ready, ready.Reason);

        var result = await adapter.SendAndObserveAsync(page, prompt, TimeSpan.FromSeconds(20), CancellationToken.None);

        Assert.Equal("Echo: Line one", result);
        Assert.DoesNotContain("Gemini said", result);
        Assert.Equal(new[] { prompt }, await page.EvaluateAsync<string[]>("() => window.sentPrompts"));
        Assert.EndsWith("/app/c0ffee1", page.Url);
    }

    [Fact]
    public async Task LaterTurn_NewReplyById_ReturnsOnlyTheNewAnswer()
    {
        var page = await OpenFakeGeminiAsync();
        var adapter = CreateAdapter();

        await adapter.SendAndObserveAsync(page, "first", TimeSpan.FromSeconds(20), CancellationToken.None);
        var second = await adapter.SendAndObserveAsync(page, "second", TimeSpan.FromSeconds(20), CancellationToken.None);

        Assert.Equal("Echo: second", second);
    }

    [Fact]
    public async Task CodeBlocks_BecomeFences_GenericLabelHasNoLanguage()
    {
        var page = await OpenFakeGeminiAsync();
        await page.EvaluateAsync(@"() => window.replyHtml = () =>
            '<p>Two blocks:</p>' +
            '<code-block><div class=""code-block-decoration""><span>Python</span><button>Copy code</button></div>' +
            '<pre><code data-test-id=""code-content"">print(1)\nprint(2)</code></pre></code-block>' +
            '<code-block><div class=""code-block-decoration""><span>Code snippet</span><button>Copy code</button></div>' +
            '<pre><code data-test-id=""code-content"">```tool_call\n{}\n```</code></pre></code-block>'");
        var adapter = CreateAdapter();

        var result = await adapter.SendAndObserveAsync(page, "code please", TimeSpan.FromSeconds(20), CancellationToken.None);

        Assert.Contains("```python\nprint(1)\nprint(2)\n```", result);
        Assert.DoesNotContain("Copy code", result);
        Assert.DoesNotContain("```codesnippet", result);
        Assert.DoesNotContain("Code snippet", result);
    }

    [Fact]
    public async Task ErrorReply_IsReportedAsUpstreamError()
    {
        var page = await OpenFakeGeminiAsync();
        await page.EvaluateAsync("() => window.replyHtml = () => '<p>I seem to be encountering an error. Can I try something else for you?</p>'");
        var adapter = CreateAdapter();

        var ex = await Assert.ThrowsAsync<UpstreamUiException>(() =>
            adapter.SendAndObserveAsync(page, "hello", TimeSpan.FromSeconds(20), CancellationToken.None));

        Assert.Contains("encountering an error", ex.Message);
    }

    [Fact]
    public async Task CheckPageReadyAsync_SignedOut_ReturnsSignInRequired()
    {
        var page = await OpenFakeGeminiAsync(PageHtml.Replace("<div id='chat'></div>", "<div id='chat'></div><a href='https://accounts.google.com/'>Sign in</a>"));
        var adapter = CreateAdapter();

        var ready = await adapter.CheckPageReadyAsync(page);

        Assert.False(ready.Ready);
        Assert.Contains("Sign-in", ready.Reason);
    }
}
