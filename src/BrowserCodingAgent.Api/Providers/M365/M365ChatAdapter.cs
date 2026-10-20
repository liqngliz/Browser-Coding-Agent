using System.Diagnostics;
using BrowserCodingAgent.Api.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;

namespace BrowserCodingAgent.Api.Providers.M365;

public class UpstreamUiException : Exception
{
    public UpstreamUiException(string message) : base(message) { }
}

public class M365ChatAdapter
{
    private readonly ChatOptions _chatOptions;
    private readonly ILogger<M365ChatAdapter> _logger;

    private static readonly string[] DefaultComposerSelectors =
    {
        "#m365-chat-editor-target-element",
        "[contenteditable='true'][aria-label='Message Copilot']",
        "textarea[placeholder*='Ask']",
        "textarea[placeholder*='Copilot']",
        "textarea#userInput",
        "textarea[data-testid*='chat-input']",
        "[contenteditable='true'][role='textbox']",
        "textarea[aria-label*='Ask']",
        "[aria-label='Ask Microsoft 365 Copilot']",
        "textarea"
    };

    private static readonly string[] DefaultSendButtonSelectors =
    {
        "button[aria-label='Submit message']",
        "button[aria-label='Send']",
        "button[aria-label*='Send']",
        "button[aria-label*='Submit']",
        "button[title*='Send']",
        "button[data-testid*='send']"
    };

    private static readonly string[] DefaultStopGeneratingSelectors =
    {
        "button[aria-label='Stop generating']",
        "button[aria-label='Stop responding']",
        "button[aria-label*='Stop']",
        "button[title*='Stop']",
        "button[data-testid*='stop']",
        "[data-testid*='stop-generating']"
    };

    private static readonly string[] DefaultAssistantMessageSelectors =
    {
        "[data-testid='markdown-reply']",
        "[data-message-author='assistant']",
        "[data-message-author='bot']",
        "[data-author='assistant']",
        "[data-content='ai-message']",
        "div[data-testid*='bot-message']",
        "div[data-testid*='copilot-message']",
        "div[data-testid*='assistant-message']",
        ".fui-ChatMessage[data-message-author='assistant']"
    };

    private static readonly string[] DefaultUserMessageSelectors =
    {
        "[data-message-author='user']",
        "[data-content='user-message']",
        "div[data-testid*='user-message']",
        ".fui-ChatMessage[data-message-author='user']"
    };

    private static readonly string[] DefaultErrorBannerSelectors =
    {
        "[role='alert']",
        "[data-testid*='error-message']",
        ".error-banner"
    };

    private static readonly TimeSpan SendEnableWait = TimeSpan.FromSeconds(10);

    public M365ChatAdapter(IOptions<ChatOptions> chatOptions, ILogger<M365ChatAdapter> logger)
    {
        _chatOptions = chatOptions.Value;
        _logger = logger;
    }

    public async Task<(bool Ready, string? Reason)> CheckPageReadyAsync(IPage page)
    {
        var (composer, _) = await FindFirstElementAsync(page, GetComposerSelectors(), 1000);
        if (composer == null || !await composer.IsVisibleAsync())
            return (false, "Composer input box is not visible on page.");

        var composerText = await GetComposerTextAsync(composer, 500);
        if (!string.IsNullOrWhiteSpace(composerText))
            return (false, "Composer already contains text. Clear composer before submitting.");

        var (stopBtn, _) = await FindFirstElementAsync(page, GetStopGeneratingSelectors(), 500);
        if (stopBtn != null && await stopBtn.IsVisibleAsync())
            return (false, "Chat is currently generating a response.");

        return (true, null);
    }

    public async Task<string> SendAndObserveAsync(
        IPage page,
        string promptText,
        TimeSpan timeout,
        CancellationToken clientCancellation,
        Action? onSendDispatched = null)
    {
        clientCancellation.ThrowIfCancellationRequested();

        // 1. Establish single monotonic deadline before any composer interaction
        var stopwatch = Stopwatch.StartNew();

        float GetRemainingBudgetMs()
        {
            var remaining = timeout - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
                throw new TimeoutException($"Response timeout of {timeout.TotalMinutes} minutes exceeded before operation completed.");
            return (float)remaining.TotalMilliseconds;
        }

        // 2. Validate composer readiness under deadline
        var (composer, _) = await FindFirstElementAsync(page, GetComposerSelectors(), GetRemainingBudgetMs());
        if (composer == null || !await composer.IsVisibleAsync())
            throw new InvalidOperationException("Composer input box was not found.");

        var composerText = await GetComposerTextAsync(composer, Math.Min(500, GetRemainingBudgetMs()));
        if (!string.IsNullOrWhiteSpace(composerText))
            throw new InvalidOperationException("Composer already contains text. Expected empty composer.");

        var (stopButtonBefore, _) = await FindFirstElementAsync(page, GetStopGeneratingSelectors(), Math.Min(300, GetRemainingBudgetMs()));
        if (stopButtonBefore != null && await stopButtonBefore.IsVisibleAsync())
            throw new InvalidOperationException("Chat is currently generating a response.");

        var conversationUrl = page.Url;
        var initialAssistantCount = await GetAssistantMessageCountAsync(page);

        clientCancellation.ThrowIfCancellationRequested();

        // 3. Fill composer and verify exact text matches
        await composer.FillAsync(promptText, new LocatorFillOptions { Timeout = GetRemainingBudgetMs() });
        var filledText = await GetComposerTextAsync(composer, Math.Min(500, GetRemainingBudgetMs()));
        if (string.IsNullOrWhiteSpace(filledText))
            throw new InvalidOperationException("Failed to populate prompt into composer.");
        if (!string.Equals(filledText.Trim(), promptText.Trim(), StringComparison.Ordinal))
            throw new InvalidOperationException("Populated prompt in composer does not match requested prompt.");

        // 4. Verify Send button
        var (sendButton, _) = await FindFirstElementAsync(page, GetSendButtonSelectors(), GetRemainingBudgetMs());
        if (sendButton == null || !await sendButton.IsVisibleAsync())
            throw new InvalidOperationException("Send button was not found or is not visible.");

        // M365 keeps Send disabled for a few seconds after the editor receives text
        var sendEnableDeadline = stopwatch.Elapsed + SendEnableWait;
        while (!await sendButton.IsEnabledAsync())
        {
            if (stopwatch.Elapsed >= sendEnableDeadline)
                throw new InvalidOperationException("Send button is disabled.");

            await Task.Delay((int)Math.Min(100, GetRemainingBudgetMs()), clientCancellation);
        }

        clientCancellation.ThrowIfCancellationRequested();

        // 5. Dispatch single Send click
        await sendButton.ClickAsync(new LocatorClickOptions
        {
            Timeout = GetRemainingBudgetMs()
        });

        onSendDispatched?.Invoke();
        _logger.LogInformation("Prompt sent to browser UI. Observing response...");

        // 6. Watch answer with exponential backoff bounded by monotonic deadline
        var currentIntervalMs = 500;
        var generationSeen = false;
        string? lastObservedText = null;
        var stableCount = 0;

        while (true)
        {
            var remaining = timeout - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
                throw new TimeoutException($"Response timeout of {timeout.TotalMinutes} minutes exceeded waiting for assistant answer.");

            if (page.IsClosed)
                throw new InvalidOperationException("Browser page closed while waiting for answer.");

            if (page.Url != conversationUrl)
            {
                // First turn of a new chat navigates /chat -> /chat/conversation/{id}
                if (!IsNewConversationNavigation(conversationUrl, page.Url))
                    throw new InvalidOperationException("Conversation or page URL changed during generation.");

                conversationUrl = page.Url;
            }

            // Check for UI error banner
            var (errorElement, _) = await FindFirstElementAsync(page, GetErrorBannerSelectors(), Math.Min(100, (float)remaining.TotalMilliseconds));
            if (errorElement != null && await errorElement.IsVisibleAsync())
            {
                var errorText = await errorElement.InnerTextAsync();
                if (!string.IsNullOrWhiteSpace(errorText))
                    throw new UpstreamUiException(errorText.Trim());
            }

            var (stopButton, _) = await FindFirstElementAsync(page, GetStopGeneratingSelectors(), Math.Min(100, (float)remaining.TotalMilliseconds));
            var isGenerating = stopButton != null && await stopButton.IsVisibleAsync();

            var (currentSendBtn, _) = await FindFirstElementAsync(page, GetSendButtonSelectors(), Math.Min(100, (float)remaining.TotalMilliseconds));
            var isSendEnabled = currentSendBtn != null && await currentSendBtn.IsEnabledAsync();

            var (currentComposer, _) = await FindFirstElementAsync(page, GetComposerSelectors(), Math.Min(100, (float)remaining.TotalMilliseconds));
            var isComposerReady = currentComposer != null && await currentComposer.IsEnabledAsync();
            var currentComposerText = currentComposer != null ? await GetComposerTextAsync(currentComposer, 50) : null;
            var isComposerEmpty = string.IsNullOrWhiteSpace(currentComposerText);

            if (isGenerating || (currentSendBtn != null && !isSendEnabled))
            {
                generationSeen = true;
                currentIntervalMs = 500; // Reset backoff on observed activity
            }

            var currentAssistantCount = await GetAssistantMessageCountAsync(page);
            if (currentAssistantCount > initialAssistantCount)
            {
                var latestText = await GetLatestAssistantMessageTextAsync(page);
                if (!string.IsNullOrWhiteSpace(latestText))
                {
                    if (latestText != lastObservedText)
                    {
                        lastObservedText = latestText;
                        currentIntervalMs = 500; // Reset backoff when text grows
                        stableCount = 0;
                    }
                    else
                    {
                        stableCount++;
                    }

                    // Completion condition requires:
                    // 1) Generation was actually observed (stop control or send disable)
                    // 2) Active generation indicator is off
                    // 3) UI has returned to idle: send button is enabled, OR composer is enabled and empty
                    // 4) Text is non-empty and stable across intervals
                    var isUiIdle = isSendEnabled || (isComposerReady && isComposerEmpty);
                    if (generationSeen && !isGenerating && isUiIdle && stableCount >= 1)
                    {
                        _logger.LogInformation("Answer collection completed successfully.");
                        return latestText;
                    }
                }
            }

            remaining = timeout - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
                throw new TimeoutException($"Response timeout of {timeout.TotalMinutes} minutes exceeded waiting for assistant answer.");

            var delayMs = (int)Math.Min((double)currentIntervalMs, remaining.TotalMilliseconds);
            if (delayMs <= 0)
                throw new TimeoutException($"Response timeout of {timeout.TotalMinutes} minutes exceeded waiting for assistant answer.");

            try
            {
                await Task.Delay(delayMs, clientCancellation);
            }
            catch (OperationCanceledException) when (clientCancellation.IsCancellationRequested)
            {
                _logger.LogWarning("Client cancelled request wait after submission. Observing browser until turn completes within remaining budget.");
                await Task.Delay(delayMs, CancellationToken.None);
            }

            currentIntervalMs = Math.Min(currentIntervalMs * 2, 5000);
        }
    }

    private IEnumerable<string> GetComposerSelectors() =>
        !string.IsNullOrWhiteSpace(_chatOptions.ComposerSelector)
            ? new[] { _chatOptions.ComposerSelector }
            : DefaultComposerSelectors;

    private IEnumerable<string> GetSendButtonSelectors() =>
        !string.IsNullOrWhiteSpace(_chatOptions.SendButtonSelector)
            ? new[] { _chatOptions.SendButtonSelector }
            : DefaultSendButtonSelectors;

    private IEnumerable<string> GetStopGeneratingSelectors() =>
        !string.IsNullOrWhiteSpace(_chatOptions.StopGeneratingSelector)
            ? new[] { _chatOptions.StopGeneratingSelector }
            : DefaultStopGeneratingSelectors;

    private IEnumerable<string> GetAssistantMessageSelectors() =>
        !string.IsNullOrWhiteSpace(_chatOptions.AssistantMessageSelector)
            ? new[] { _chatOptions.AssistantMessageSelector }
            : DefaultAssistantMessageSelectors;

    private IEnumerable<string> GetErrorBannerSelectors() => DefaultErrorBannerSelectors;

    private static bool IsNewConversationNavigation(string fromUrl, string toUrl)
    {
        if (!Uri.TryCreate(fromUrl, UriKind.Absolute, out var from) ||
            !Uri.TryCreate(toUrl, UriKind.Absolute, out var to))
            return false;

        return string.Equals(from.Host, to.Host, StringComparison.OrdinalIgnoreCase) &&
               !from.AbsolutePath.Contains("/conversation/", StringComparison.OrdinalIgnoreCase) &&
               to.AbsolutePath.Contains("/conversation/", StringComparison.OrdinalIgnoreCase);
    }

    // The M365 editor appends zero-width markers (U+200B, U+200C) after typed text
    private static string NormalizeComposerText(string? text) =>
        new string((text ?? string.Empty).Where(c => c is not ('​' or '‌' or '‍' or '﻿')).ToArray()).Trim();

    private static async Task<(ILocator? Locator, string? Selector)> FindFirstElementAsync(
        IPage page,
        IEnumerable<string> selectors,
        float timeoutMs)
    {
        if (timeoutMs <= 0)
            return (null, null);

        var selectorList = selectors.ToList();
        var visible = new LocatorFilterOptions { Visible = true };

        // One wait across all selectors, so selectors absent from the page do not each consume budget
        var any = selectorList.Select(sel => page.Locator(sel)).Aggregate((a, b) => a.Or(b));
        try
        {
            await any.Filter(visible).First.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = timeoutMs
            });
        }
        catch
        {
            return (null, null);
        }

        // Highest-priority selector that has a visible match
        foreach (var sel in selectorList)
        {
            var loc = page.Locator(sel).Filter(visible).First;
            try
            {
                if (await loc.IsVisibleAsync())
                    return (loc, sel);
            }
            catch
            {
                // Ignored - try next selector
            }
        }
        return (null, null);
    }

    private static async Task<string> GetComposerTextAsync(ILocator composer, float timeoutMs = 500)
    {
        try
        {
            var val = await composer.InputValueAsync(new LocatorInputValueOptions { Timeout = timeoutMs });
            if (!string.IsNullOrEmpty(val))
                return NormalizeComposerText(val);
        }
        catch
        {
            // Fallback for contenteditable div
        }

        try
        {
            return NormalizeComposerText(await composer.InnerTextAsync(new LocatorInnerTextOptions { Timeout = timeoutMs }));
        }
        catch
        {
            return string.Empty;
        }
    }

    private async Task<int> GetAssistantMessageCountAsync(IPage page)
    {
        foreach (var sel in GetAssistantMessageSelectors())
        {
            var loc = page.Locator(sel);
            try
            {
                var count = await loc.CountAsync();
                if (count > 0)
                    return count;
            }
            catch
            {
                // Ignored
            }
        }
        return 0;
    }

    private async Task<string> GetLatestAssistantMessageTextAsync(IPage page)
    {
        foreach (var sel in GetAssistantMessageSelectors())
        {
            var loc = page.Locator(sel);
            try
            {
                var count = await loc.CountAsync();
                if (count > 0)
                {
                    var last = loc.Nth(count - 1);
                    // Extract text preserving markdown code blocks, paragraphs, and list items
                    try
                    {
                        var extracted = await last.EvaluateAsync<string>(@"el => {
                            const clone = el.cloneNode(true);
                            clone.querySelectorAll('pre').forEach(pre => {
                                const code = pre.querySelector('code');
                                const lang = code ? (code.className.match(/language-(\w+)/)?.[1] || '') : '';
                                const text = (code || pre).textContent || '';
                                pre.replaceWith(document.createTextNode('\n```' + lang + '\n' + text.trimEnd() + '\n```\n'));
                            });
                            clone.querySelectorAll('p, div, li, tr').forEach(block => {
                                block.after(document.createTextNode('\n'));
                            });
                            clone.querySelectorAll('br').forEach(br => {
                                br.replaceWith(document.createTextNode('\n'));
                            });
                            return (clone.textContent || '').trim();
                        }");

                        if (!string.IsNullOrWhiteSpace(extracted))
                            return extracted;
                    }
                    catch
                    {
                        // Fallback to simple InnerTextAsync
                    }

                    var text = await last.InnerTextAsync();
                    if (!string.IsNullOrWhiteSpace(text))
                        return text.Trim();
                }
            }
            catch
            {
                // Ignored
            }
        }
        return string.Empty;
    }
}
