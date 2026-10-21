using BrowserCodingAgent.Api.Configuration;
using BrowserCodingAgent.Api.Providers.M365;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;

namespace BrowserCodingAgent.Tests;

public class SimulatedChatTests : IAsyncLifetime
{
    private IPlaywright? _playwright;
    private IBrowser? _browser;

    public async Task InitializeAsync()
    {
        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true
        });
    }

    public async Task DisposeAsync()
    {
        if (_browser != null)
            await _browser.DisposeAsync();
        _playwright?.Dispose();
    }

    private M365ChatAdapter CreateAdapter(int timeoutMinutes = 1, int sendEnableSeconds = 30)
    {
        var options = Options.Create(new ChatOptions
        {
            ModelAlias = "m365-active",
            ResponseTimeoutMinutes = timeoutMinutes,
            SendEnableSeconds = sendEnableSeconds
        });
        return new M365ChatAdapter(options, NullLogger<M365ChatAdapter>.Instance);
    }

    [Fact]
    public async Task SendAndObserveAsync_HappyPath_ExtractsMarkdownCodeFences()
    {
        var page = await _browser!.NewPageAsync();
        var html = @"
<!DOCTYPE html>
<html>
<body>
  <div id='messages'></div>
  <textarea placeholder='Ask Microsoft 365 Copilot'></textarea>
  <button aria-label='Submit message'>Send</button>
  <button aria-label='Stop generating' style='display:none'>Stop</button>
  <script>
    const composer = document.querySelector('textarea');
    const sendBtn = document.querySelector(""button[aria-label='Submit message']"");
    const stopBtn = document.querySelector(""button[aria-label='Stop generating']"");
    const messages = document.getElementById('messages');

    sendBtn.addEventListener('click', () => {
      sendBtn.disabled = true;
      stopBtn.style.display = 'inline-block';
      const prompt = composer.value;
      composer.value = '';

      setTimeout(() => {
        const turn = document.createElement('div');
        turn.setAttribute('data-message-author', 'assistant');
        turn.innerHTML = '<p>Here is your code:</p><pre><code class=""language-csharp"">int result = 42;</code></pre>';
        messages.appendChild(turn);

        stopBtn.style.display = 'none';
        sendBtn.disabled = false;
      }, 400);
    });
  </script>
</body>
</html>";
        await page.SetContentAsync(html);

        var adapter = CreateAdapter();
        var prompt = "Give me code";
        var result = await adapter.SendAndObserveAsync(page, prompt, TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.Contains("Here is your code:", result);
        Assert.Contains("```csharp", result);
        Assert.Contains("int result = 42;", result);
    }

    [Fact]
    public async Task SendAndObserveAsync_ParagraphsAndLists_PreservesLineSeparators()
    {
        var page = await _browser!.NewPageAsync();
        var html = @"
<!DOCTYPE html>
<html>
<body>
  <div id='messages'></div>
  <textarea placeholder='Ask Microsoft 365 Copilot'></textarea>
  <button aria-label='Submit message'>Send</button>
  <button aria-label='Stop generating' style='display:none'>Stop</button>
  <script>
    const sendBtn = document.querySelector(""button[aria-label='Submit message']"");
    const stopBtn = document.querySelector(""button[aria-label='Stop generating']"");
    const messages = document.getElementById('messages');

    sendBtn.addEventListener('click', () => {
      sendBtn.disabled = true;
      stopBtn.style.display = 'inline-block';
      setTimeout(() => {
        const turn = document.createElement('div');
        turn.setAttribute('data-message-author', 'assistant');
        turn.innerHTML = '<p>First paragraph.</p><p>Second paragraph.</p><ul><li>Item A</li><li>Item B</li></ul>';
        messages.appendChild(turn);

        stopBtn.style.display = 'none';
        sendBtn.disabled = false;
      }, 300);
    });
  </script>
</body>
</html>";
        await page.SetContentAsync(html);

        var adapter = CreateAdapter();
        var result = await adapter.SendAndObserveAsync(page, "Format test", TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.Contains("First paragraph.", result);
        Assert.Contains("Second paragraph.", result);
        Assert.Contains("Item A", result);
        Assert.Contains("Item B", result);
        // Verify block elements are not smushed together
        Assert.DoesNotContain("paragraph.Second", result);
        Assert.DoesNotContain("Item AItem B", result);
    }

    [Fact]
    public async Task SendAndObserveAsync_WhenSendDisabledOnEmptyComposer_CompletesSuccessfully()
    {
        var page = await _browser!.NewPageAsync();
        var html = @"
<!DOCTYPE html>
<html>
<body>
  <div id='messages'></div>
  <textarea placeholder='Ask Microsoft 365 Copilot'></textarea>
  <button aria-label='Submit message'>Send</button>
  <button aria-label='Stop generating' style='display:none'>Stop</button>
  <script>
    const composer = document.querySelector('textarea');
    const sendBtn = document.querySelector(""button[aria-label='Submit message']"");
    const stopBtn = document.querySelector(""button[aria-label='Stop generating']"");
    const messages = document.getElementById('messages');

    sendBtn.addEventListener('click', () => {
      // Clear composer and leave Send disabled because composer is now empty
      composer.value = '';
      sendBtn.disabled = true;
      stopBtn.style.display = 'inline-block';

      setTimeout(() => {
        const turn = document.createElement('div');
        turn.setAttribute('data-message-author', 'assistant');
        turn.innerText = 'FINAL ANSWER';
        messages.appendChild(turn);

        // Generation finishes: stop button disappears, composer becomes enabled, send button remains disabled
        stopBtn.style.display = 'none';
        composer.disabled = false;
      }, 300);
    });
  </script>
</body>
</html>";
        await page.SetContentAsync(html);

        var adapter = CreateAdapter();
        var result = await adapter.SendAndObserveAsync(page, "Test prompt", TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal("FINAL ANSWER", result);
    }

    [Fact]
    public async Task SendAndObserveAsync_UnknownGenerationState_DoesNotReturnPartialEarly()
    {
        var page = await _browser!.NewPageAsync();
        var html = @"
<!DOCTYPE html>
<html>
<body>
  <div id='messages'></div>
  <textarea placeholder='Ask Microsoft 365 Copilot'></textarea>
  <button aria-label='Submit message'>Send</button>
  <script>
    const sendBtn = document.querySelector(""button[aria-label='Submit message']"");
    const messages = document.getElementById('messages');

    sendBtn.addEventListener('click', () => {
      // Never shows stop button; appends PARTIAL immediately and final 5 seconds later
      const turn = document.createElement('div');
      turn.setAttribute('data-message-author', 'assistant');
      turn.innerText = 'PARTIAL';
      messages.appendChild(turn);

      setTimeout(() => {
        turn.innerText = 'FINAL COMPLETE ANSWER';
      }, 5000);
    });
  </script>
</body>
</html>";
        await page.SetContentAsync(html);

        var adapter = CreateAdapter();
        // Since no generation indicators were ever observed, it must NOT return PARTIAL after 500ms
        await Assert.ThrowsAsync<TimeoutException>(() =>
            adapter.SendAndObserveAsync(page, "Test prompt", TimeSpan.FromMilliseconds(1500), CancellationToken.None));
    }

    [Fact]
    public async Task SendAndObserveAsync_UserMessageOnly_DoesNotReturnUserPrompt()
    {
        var page = await _browser!.NewPageAsync();
        var html = @"
<!DOCTYPE html>
<html>
<body>
  <div id='messages'></div>
  <textarea placeholder='Ask Microsoft 365 Copilot'></textarea>
  <button aria-label='Submit message'>Send</button>
  <button aria-label='Stop generating' style='display:none'>Stop</button>
  <script>
    const composer = document.querySelector('textarea');
    const sendBtn = document.querySelector(""button[aria-label='Submit message']"");
    const messages = document.getElementById('messages');

    sendBtn.addEventListener('click', () => {
      sendBtn.disabled = true;
      const text = composer.value;
      composer.value = '';

      // Only create user message turn, NO assistant turn
      const userTurn = document.createElement('div');
      userTurn.className = 'fui-ChatMessage';
      userTurn.setAttribute('data-message-author', 'user');
      userTurn.innerText = text;
      messages.appendChild(userTurn);

      setTimeout(() => {
        sendBtn.disabled = false;
      }, 300);
    });
  </script>
</body>
</html>";
        await page.SetContentAsync(html);

        var adapter = CreateAdapter();
        var prompt = "USER PROMPT ONLY";

        // Must time out because no assistant turn was ever produced
        await Assert.ThrowsAsync<TimeoutException>(() =>
            adapter.SendAndObserveAsync(page, prompt, TimeSpan.FromMilliseconds(1200), CancellationToken.None));
    }

    [Fact]
    public async Task SendAndObserveAsync_PreCancelledToken_DoesNotClickSend()
    {
        var page = await _browser!.NewPageAsync();
        var html = @"
<!DOCTYPE html>
<html>
<body>
  <textarea placeholder='Ask Microsoft 365 Copilot'></textarea>
  <button aria-label='Submit message'>Send</button>
  <script>
    window.sendClicked = false;
    document.querySelector(""button[aria-label='Submit message']"").addEventListener('click', () => {
      window.sendClicked = true;
    });
  </script>
</body>
</html>";
        await page.SetContentAsync(html);

        var adapter = CreateAdapter();
        var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            adapter.SendAndObserveAsync(page, "Test prompt", TimeSpan.FromSeconds(5), cts.Token));

        var clicked = await page.EvaluateAsync<bool>("window.sendClicked");
        Assert.False(clicked);
    }

    [Fact]
    public async Task SendAndObserveAsync_ResponseTimeout_BoundsInteraction()
    {
        var page = await _browser!.NewPageAsync();
        var html = @"
<!DOCTYPE html>
<html>
<body>
  <textarea placeholder='Ask Microsoft 365 Copilot' disabled></textarea>
  <button aria-label='Submit message'>Send</button>
</body>
</html>";
        await page.SetContentAsync(html);

        var adapter = CreateAdapter();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Pass a short 200ms budget to verify that the fill attempt fails within budget
        await Assert.ThrowsAnyAsync<Exception>(() =>
            adapter.SendAndObserveAsync(page, "Test prompt", TimeSpan.FromMilliseconds(200), CancellationToken.None));

        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 1000, $"Operation took {sw.ElapsedMilliseconds}ms, exceeding budget.");
    }

    [Fact]
    public async Task SendAndObserveAsync_WhenUpstreamUiError_ThrowsUpstreamUiException()
    {
        var page = await _browser!.NewPageAsync();
        var html = @"
<!DOCTYPE html>
<html>
<body>
  <textarea placeholder='Ask Microsoft 365 Copilot'></textarea>
  <button aria-label='Submit message'>Send</button>
  <button aria-label='Stop generating' style='display:none'>Stop</button>
  <div id='err' role='alert' style='display:none'></div>
  <script>
    const sendBtn = document.querySelector(""button[aria-label='Submit message']"");
    sendBtn.addEventListener('click', () => {
      sendBtn.disabled = true;
      setTimeout(() => {
        const err = document.getElementById('err');
        err.innerText = 'Service busy. Please try again later.';
        err.style.display = 'block';
      }, 300);
    });
  </script>
</body>
</html>";
        await page.SetContentAsync(html);

        var adapter = CreateAdapter();
        var ex = await Assert.ThrowsAsync<UpstreamUiException>(() =>
            adapter.SendAndObserveAsync(page, "Test prompt", TimeSpan.FromSeconds(5), CancellationToken.None));

        Assert.Equal("Service busy. Please try again later.", ex.Message);
    }

    [Fact]
    public async Task CheckPageReadyAsync_WhenComposerNotEmpty_ReturnsNotReady()
    {
        var page = await _browser!.NewPageAsync();
        var html = @"
<!DOCTYPE html>
<html>
<body>
  <textarea placeholder='Ask Microsoft 365 Copilot'>Already typed text</textarea>
  <button aria-label='Submit message'>Send</button>
</body>
</html>";
        await page.SetContentAsync(html);

        var adapter = CreateAdapter();
        var (ready, reason) = await adapter.CheckPageReadyAsync(page);

        Assert.False(ready);
        Assert.Contains("already contains text", reason);
    }

    [Fact]
    public async Task CheckPageReadyAsync_WhenGenerating_ReturnsNotReady()
    {
        var page = await _browser!.NewPageAsync();
        var html = @"
<!DOCTYPE html>
<html>
<body>
  <textarea placeholder='Ask Microsoft 365 Copilot'></textarea>
  <button aria-label='Submit message'>Send</button>
  <button aria-label='Stop generating' style='display:inline-block'>Stop</button>
</body>
</html>";
        await page.SetContentAsync(html);

        var adapter = CreateAdapter();
        var (ready, reason) = await adapter.CheckPageReadyAsync(page);

        Assert.False(ready);
        Assert.Contains("currently generating", reason);
    }

    [Fact]
    public async Task SendAndObserveAsync_VirtualizedFeed_DetectsNewReplyWhenCountStaysFlat()
    {
        var page = await _browser!.NewPageAsync();
        var html = @"
<!DOCTYPE html>
<html>
<body>
  <div id='feed'>
    <div data-testid='markdown-reply' data-message-id='m1' aria-hidden='false'><p>old one</p></div>
    <div data-testid='markdown-reply' data-message-id='m2' aria-hidden='false'><p>old two</p></div>
    <div data-testid='markdown-reply' data-message-id='m3' aria-hidden='false'><p>old three</p></div>
    <div data-testid='markdown-reply' data-message-id='m4' aria-hidden='false'><p>old four</p></div>
  </div>
  <textarea placeholder='Ask Microsoft 365 Copilot'></textarea>
  <button aria-label='Submit message'>Send</button>
  <button aria-label='Stop generating' style='display:none'>Stop</button>
  <script>
    const feed = document.getElementById('feed');
    const stop = document.querySelector(""button[aria-label='Stop generating']"");
    document.querySelector(""button[aria-label='Submit message']"").addEventListener('click', () => {
      document.querySelector('textarea').value = '';
      // Virtualizer: the oldest reply leaves the DOM as the new one arrives, so the count stays at 4
      feed.firstElementChild.remove();
      const reply = document.createElement('div');
      reply.setAttribute('data-testid', 'markdown-reply');
      reply.setAttribute('aria-hidden', 'true');
      feed.append(reply);
      stop.style.display = 'inline-block';
      setTimeout(() => {
        reply.innerHTML = '<p>NEW ANSWER</p>';
        reply.setAttribute('data-message-id', 'm5');
        reply.setAttribute('aria-hidden', 'false');
        stop.style.display = 'none';
      }, 400);
    });
  </script>
</body>
</html>";
        await page.SetContentAsync(html);

        var adapter = CreateAdapter();
        var result = await adapter.SendAndObserveAsync(page, "Next question", TimeSpan.FromSeconds(20), CancellationToken.None);

        Assert.Equal("NEW ANSWER", result);
    }

    [Fact]
    public async Task SendAndObserveAsync_RepliesWithoutIds_FallBackToCounting()
    {
        var page = await _browser!.NewPageAsync();
        var html = @"
<!DOCTYPE html>
<html>
<body>
  <div id='feed'><div data-testid='markdown-reply' aria-hidden='false'><p>first answer</p></div></div>
  <textarea placeholder='Ask Microsoft 365 Copilot'></textarea>
  <button aria-label='Submit message'>Send</button>
  <button aria-label='Stop generating' style='display:none'>Stop</button>
  <script>
    const stop = document.querySelector(""button[aria-label='Stop generating']"");
    document.querySelector(""button[aria-label='Submit message']"").addEventListener('click', () => {
      document.querySelector('textarea').value = '';
      const reply = document.createElement('div');
      reply.setAttribute('data-testid', 'markdown-reply');
      reply.setAttribute('aria-hidden', 'true');
      document.getElementById('feed').append(reply);
      stop.style.display = 'inline-block';
      setTimeout(() => {
        reply.innerHTML = '<p>second answer</p>';
        reply.setAttribute('aria-hidden', 'false');
        stop.style.display = 'none';
      }, 400);
    });
  </script>
</body>
</html>";
        await page.SetContentAsync(html);

        var adapter = CreateAdapter();
        var result = await adapter.SendAndObserveAsync(page, "Next question", TimeSpan.FromSeconds(20), CancellationToken.None);

        Assert.Equal("second answer", result);
    }

    [Fact]
    public async Task SendAndObserveAsync_ReadbackMismatch_ClearsComposerAndDoesNotSend()
    {
        // An editor that rewrites input: readback never matches, so nothing may be sent and the composer must not stay filled
        var page = await _browser!.NewPageAsync();
        var html = @"
<!DOCTYPE html>
<html>
<body>
  <textarea placeholder='Ask Microsoft 365 Copilot'></textarea>
  <button aria-label='Submit message'>Send</button>
  <script>
    window.sendClicked = false;
    const composer = document.querySelector('textarea');
    composer.addEventListener('input', () => { if (composer.value) composer.value = composer.value.toUpperCase(); });
    document.querySelector(""button[aria-label='Submit message']"").addEventListener('click', () => { window.sendClicked = true; });
  </script>
</body>
</html>";
        await page.SetContentAsync(html);

        var adapter = CreateAdapter();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            adapter.SendAndObserveAsync(page, "lower case prompt", TimeSpan.FromSeconds(20), CancellationToken.None));

        Assert.Contains("does not match", ex.Message);
        Assert.False(await page.EvaluateAsync<bool>("window.sendClicked"));
        Assert.Equal(string.Empty, await page.EvaluateAsync<string>("document.querySelector('textarea').value"));

        var (ready, _) = await adapter.CheckPageReadyAsync(page);
        Assert.True(ready);
    }

    // submit(): what the page does when it accepts a message (clear composer, show Stop, reply after 300ms)
    private const string SubmitScript = @"
    window.processed = 0;
    const composer = document.querySelector('textarea');
    const sendBtn = document.querySelector(""button[aria-label='Submit message']"");
    const stopBtn = document.querySelector(""button[aria-label='Stop generating']"");
    function submit() {
      window.processed++;
      composer.value = '';
      stopBtn.style.display = 'inline-block';
      setTimeout(() => {
        const turn = document.createElement('div');
        turn.setAttribute('data-message-author', 'assistant');
        turn.innerText = 'ANSWER';
        document.getElementById('messages').appendChild(turn);
        stopBtn.style.display = 'none';
      }, 300);
    }";

    private static string SendPage(string behavior) => @"
<!DOCTYPE html>
<html>
<body>
  <div id='messages'></div>
  <textarea placeholder='Ask Microsoft 365 Copilot'></textarea>
  <button aria-label='Submit message'>Send</button>
  <button aria-label='Stop generating' style='display:none'>Stop</button>
  <script>" + SubmitScript + behavior + @"
  </script>
</body>
</html>";

    [Fact]
    public async Task SendAndObserveAsync_LostFirstClick_IsRetried_AndSentOnce()
    {
        var page = await _browser!.NewPageAsync();
        await page.SetContentAsync(SendPage(@"
    window.clicks = 0;
    sendBtn.addEventListener('click', () => { if (++window.clicks > 1) submit(); });"));

        var result = await CreateAdapter().SendAndObserveAsync(page, "Hello", TimeSpan.FromSeconds(60), CancellationToken.None);

        Assert.Equal("ANSWER", result);
        Assert.Equal(2, await page.EvaluateAsync<int>("window.clicks"));
        Assert.Equal(1, await page.EvaluateAsync<int>("window.processed"));
    }

    [Fact]
    public async Task SendAndObserveAsync_ClicksIgnored_EnterSubmits()
    {
        var page = await _browser!.NewPageAsync();
        await page.SetContentAsync(SendPage(@"
    composer.addEventListener('keydown', e => { if (e.key === 'Enter') { e.preventDefault(); submit(); } });"));

        var result = await CreateAdapter().SendAndObserveAsync(page, "Hello", TimeSpan.FromSeconds(60), CancellationToken.None);

        Assert.Equal("ANSWER", result);
        Assert.Equal(1, await page.EvaluateAsync<int>("window.processed"));
    }

    [Fact]
    public async Task SendAndObserveAsync_StalledEditor_RefillEnablesSend()
    {
        // Send enables only after a second input event, like an editor that missed the first fill
        var page = await _browser!.NewPageAsync();
        await page.SetContentAsync(SendPage(@"
    window.inputs = 0;
    sendBtn.disabled = true;
    composer.addEventListener('input', () => { if (composer.value && ++window.inputs >= 2) sendBtn.disabled = false; });
    sendBtn.addEventListener('click', submit);"));

        var result = await CreateAdapter(sendEnableSeconds: 6).SendAndObserveAsync(page, "Hello", TimeSpan.FromSeconds(60), CancellationToken.None);

        Assert.Equal("ANSWER", result);
        Assert.True(await page.EvaluateAsync<int>("window.inputs") >= 2);
    }

    [Fact]
    public async Task SendAndObserveAsync_SendNeverEnables_FocusAndEnterSubmits()
    {
        // Send stays disabled (network stall); the composer still submits on Enter
        var page = await _browser!.NewPageAsync();
        await page.SetContentAsync(SendPage(@"
    sendBtn.disabled = true;
    composer.addEventListener('keydown', e => { if (e.key === 'Enter') { e.preventDefault(); submit(); } });"));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await CreateAdapter(sendEnableSeconds: 6).SendAndObserveAsync(page, "Hello", TimeSpan.FromSeconds(60), CancellationToken.None);

        Assert.Equal("ANSWER", result);
        Assert.Equal(1, await page.EvaluateAsync<int>("window.processed"));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15), $"took {sw.Elapsed.TotalSeconds:F1}s");
    }

    [Fact]
    public async Task SendAndObserveAsync_NeverAccepted_FailsCleanly()
    {
        var page = await _browser!.NewPageAsync();
        await page.SetContentAsync(SendPage(""));

        var adapter = CreateAdapter();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            adapter.SendAndObserveAsync(page, "Hello", TimeSpan.FromSeconds(90), CancellationToken.None));

        Assert.Contains("did not accept the message after 3 send attempts", ex.Message);
        Assert.Equal(0, await page.EvaluateAsync<int>("window.processed"));
        Assert.Equal(string.Empty, await page.EvaluateAsync<string>("document.querySelector('textarea').value"));
        var (ready, _) = await adapter.CheckPageReadyAsync(page);
        Assert.True(ready);
    }

    [Fact]
    public async Task CheckPageReadyAsync_WhenDailyLimitReached_ReturnsNotReady()
    {
        var page = await _browser!.NewPageAsync();
        var html = @"
<!DOCTYPE html>
<html>
<body>
  <div role='status'>You've reached your daily limit<br>Get more usage now or check back at 2:00am</div>
  <textarea placeholder='Ask Microsoft 365 Copilot'></textarea>
  <button aria-label='Submit message'>Send</button>
</body>
</html>";
        await page.SetContentAsync(html);

        var adapter = CreateAdapter();
        var (ready, reason) = await adapter.CheckPageReadyAsync(page);

        Assert.False(ready);
        Assert.Contains("usage limit reached", reason);
        Assert.Contains("check back at 2:00am", reason);
    }

    [Fact]
    public async Task SendAndObserveAsync_WhenDailyLimitShownAfterSend_ThrowsUpstreamUiExceptionWithoutWaiting()
    {
        var page = await _browser!.NewPageAsync();
        var html = @"
<!DOCTYPE html>
<html>
<body>
  <div id='messages'></div>
  <textarea placeholder='Ask Microsoft 365 Copilot'></textarea>
  <button aria-label='Submit message'>Send</button>
  <script>
    document.querySelector(""button[aria-label='Submit message']"").addEventListener('click', () => {
      document.querySelector('textarea').value = '';
      const banner = document.createElement('div');
      banner.setAttribute('role', 'status');
      banner.innerText = ""You've reached your daily limit. Get more usage now or check back at 2:00am"";
      document.body.append(banner);
    });
  </script>
</body>
</html>";
        await page.SetContentAsync(html);

        var adapter = CreateAdapter();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<UpstreamUiException>(() =>
            adapter.SendAndObserveAsync(page, "Test prompt", TimeSpan.FromMinutes(1), CancellationToken.None));

        Assert.Contains("daily limit", ex.Message);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"Took {sw.Elapsed.TotalSeconds:F1}s; expected an immediate failure.");
    }
}
