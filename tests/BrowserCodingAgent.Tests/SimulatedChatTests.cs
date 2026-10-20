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

    private M365ChatAdapter CreateAdapter(int timeoutMinutes = 1)
    {
        var options = Options.Create(new ChatOptions
        {
            ModelAlias = "m365-active",
            ResponseTimeoutMinutes = timeoutMinutes
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
}
