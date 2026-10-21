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

// The composer refused the full prompt before anything was sent: it kept only a prefix (AcceptedLength), or Send
// stayed disabled on a long prompt (AcceptedLength null). The caller can resend the prompt in chunks.
public class PromptTooLongException : InvalidOperationException
{
    public PromptTooLongException(string message, int promptLength, int? acceptedLength) : base(message)
    {
        PromptLength = promptLength;
        AcceptedLength = acceptedLength;
    }

    public int PromptLength { get; }
    public int? AcceptedLength { get; }
}

public class M365ChatAdapter
{
    private readonly ChatOptions _chatOptions;
    private readonly ILogger<M365ChatAdapter> _logger;

    private const string M365ComposerSelector = "#m365-chat-editor-target-element";
    private const string M365ReplySelector = "[data-testid='markdown-reply']";

    private static readonly string[] DefaultComposerSelectors =
    {
        M365ComposerSelector,
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
        M365ReplySelector,
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
        "[data-testid='chatQuestion']",
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

    private static readonly string[] DefaultNewChatSelectors =
    {
        "a[aria-label='New chat']",
        "button[aria-label='New chat']",
        "[data-testid*='new-chat']"
    };

    // A click lost to a re-render or network stall leaves the prompt in the composer; confirm within this window
    private static readonly TimeSpan SendConfirmWait = TimeSpan.FromSeconds(5);
    private const int MaxSendAttempts = 3; // click, click, then focus + Enter in the composer
    private static readonly TimeSpan EnterFallbackAfter = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan NewChatWait = TimeSpan.FromSeconds(20);
    private const int MaxFillAttempts = 3;
    // Some M365 accounts cap the composer at 8000 characters; a disabled Send above this length means "too long"
    private const int LongPromptChars = 8000;
    private const int MaxPollIntervalMs = 5000;
    private const int MaxStreamingPollIntervalMs = 500;

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

        var usageLimit = await FindUsageLimitNoticeAsync(page);
        if (usageLimit != null)
            return (false, $"Copilot usage limit reached: {usageLimit}");

        return (true, null);
    }

    public async Task<string> SendAndObserveAsync(
        IPage page,
        string promptText,
        TimeSpan timeout,
        CancellationToken clientCancellation,
        Action? onSendDispatched = null,
        Func<string, Task>? onProgress = null)
    {
        clientCancellation.ThrowIfCancellationRequested();

        // Editors store line breaks as \n, so CRLF text (Windows files, tool output) would never match on readback.
        // The editor also drops zero-width characters and BOMs (Visual Studio saves .cs/.csproj with U+FEFF), and
        // the readback strips them too, so the prompt must lose them as well or the comparison can never succeed.
        promptText = StripInvisible(promptText.Replace("\r\n", "\n").Replace('\r', '\n'));

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
        // M365 virtualizes long chats (older replies leave the DOM as new ones arrive), so the count can stay flat.
        // When the newest reply has an identity, the answer is the newest reply once that identity changes.
        var initialLatestReplyId = await GetLatestReplyIdAsync(page);
        // Only a finished reply with a real id anchors the comparison; replies without ids fall back to counting
        var useReplyIds = !string.IsNullOrEmpty(initialLatestReplyId);

        clientCancellation.ThrowIfCancellationRequested();

        var initialUserCount = await GetUserMessageCountAsync(page);
        var submittedByEnter = false;

        // Waits up to Chat:SendEnableSeconds for an enabled Send (M365 needs ~3s after a fill; longer on a slow network).
        // At 10s (or half the wait) a still-disabled Send gets focus + Enter in the composer, which can submit on its
        // own; at two thirds, one refill re-runs a stalled editor's input handling.
        async Task<(ILocator? Button, bool Seen, bool Wiped, bool Submitted)> WaitForEnabledSendAsync()
        {
            var seen = false;
            var enterTried = false;
            var refilled = false;
            var started = stopwatch.Elapsed;
            var wait = TimeSpan.FromSeconds(_chatOptions.SendEnableSeconds);
            var enterAfter = TimeSpan.FromTicks(Math.Min(EnterFallbackAfter.Ticks, wait.Ticks / 2));
            var enterSpent = TimeSpan.Zero; // the Enter attempt's own confirmation wait does not count against the Send wait
            while (stopwatch.Elapsed - started - enterSpent < wait)
            {
                var (candidate, _) = await FindFirstElementAsync(page, GetSendButtonSelectors(), Math.Min(250, GetRemainingBudgetMs()));
                if (candidate != null)
                {
                    seen = true;
                    if (await candidate.IsEnabledAsync())
                        return (candidate, true, false, false);
                }

                if (string.IsNullOrWhiteSpace(await GetComposerTextAsync(composer, Math.Min(500, GetRemainingBudgetMs()))))
                    return (null, seen, true, false);

                var waited = stopwatch.Elapsed - started - enterSpent;
                if (!enterTried && waited > enterAfter)
                {
                    enterTried = true;
                    _logger.LogWarning("Send still disabled after {Seconds:F0}s; focusing the composer and pressing Enter.", waited.TotalSeconds);
                    var enterStarted = stopwatch.Elapsed;
                    if (await PressEnterInComposerAsync(composer, GetRemainingBudgetMs()) &&
                        await WaitForSubmissionAsync(page, composer, promptText, initialUserCount, initialAssistantCount,
                            initialLatestReplyId, useReplyIds, conversationUrl, clientCancellation))
                        return (null, seen, false, true);
                    enterSpent = stopwatch.Elapsed - enterStarted;
                    continue; // re-check Send right away: the key press itself can enable it
                }
                else if (!refilled && waited > wait * 2 / 3)
                {
                    refilled = true;
                    _logger.LogWarning("Send still disabled after {Seconds:F0}s; filling the composer again to retrigger the editor.", waited.TotalSeconds);
                    await composer.FillAsync(promptText, new LocatorFillOptions { Timeout = GetRemainingBudgetMs() });
                }

                await Task.Delay((int)Math.Min(100, GetRemainingBudgetMs()), clientCancellation);
            }

            return (null, seen, false, false);
        }

        // 3-4. Fill, verify exact text, then wait for an enabled Send.
        // M365 keeps Send disabled for a few seconds after the editor receives text, and a freshly loaded
        // (server-rendered) page can wipe the editor when its scripts attach: text that vanishes is filled again.
        ILocator? sendButton = null;
        try
        {
            for (var attempt = 1; sendButton == null && !submittedByEnter; attempt++)
            {
                await composer.FillAsync(promptText, new LocatorFillOptions { Timeout = GetRemainingBudgetMs() });
                var filledText = await GetComposerTextAsync(composer, Math.Min(500, GetRemainingBudgetMs()));
                if (string.IsNullOrWhiteSpace(filledText))
                    throw new InvalidOperationException("Failed to populate prompt into composer.");
                if (!string.Equals(filledText.Trim(), promptText.Trim(), StringComparison.Ordinal))
                {
                    var accepted = filledText.Trim();
                    if (accepted.Length < promptText.Trim().Length && promptText.Trim().StartsWith(accepted, StringComparison.Ordinal))
                        throw new PromptTooLongException(
                            $"Composer kept only {accepted.Length} of {promptText.Trim().Length} characters (input limit).",
                            promptText.Trim().Length, accepted.Length);
                    throw new InvalidOperationException("Populated prompt in composer does not match requested prompt.");
                }

                var (enabledSend, sendSeen, wiped, submitted) = await WaitForEnabledSendAsync();
                sendButton = enabledSend;
                submittedByEnter = submitted;
                if (sendButton != null || submittedByEnter)
                    break;

                if (!wiped && sendSeen && promptText.Length > LongPromptChars)
                    throw new PromptTooLongException(
                        $"Send stayed disabled for a {promptText.Length}-character prompt (input limit).", promptText.Length, null);

                if (!wiped || attempt >= MaxFillAttempts)
                    throw new InvalidOperationException(
                        wiped ? $"Composer text was cleared by the page after {attempt} fill attempts."
                        : sendSeen ? "Send button is disabled."
                        : "Send button was not found or is not visible.");

                _logger.LogWarning("Composer text was cleared after filling (page still initializing); filling again ({Attempt}/{MaxAttempts}).",
                    attempt + 1, MaxFillAttempts);
            }

            // 5. Click Send, then confirm M365 took the message: the composer still holding the exact prompt means the
            // send failed (a click lost to a re-render or network stall). Retried (the last attempt is focus + Enter),
            // never repeated once accepted.
            for (var sendAttempt = 1; !submittedByEnter; sendAttempt++)
            {
                clientCancellation.ThrowIfCancellationRequested();
                try
                {
                    if (sendAttempt < MaxSendAttempts)
                        await sendButton!.ClickAsync(new LocatorClickOptions { Timeout = Math.Min(5000, GetRemainingBudgetMs()) });
                    else
                        await PressEnterInComposerAsync(composer, GetRemainingBudgetMs());
                }
                catch (Exception ex) when ((ex is PlaywrightException || ex is TimeoutException) && sendAttempt < MaxSendAttempts)
                {
                    _logger.LogWarning("Send attempt {Attempt} failed: {Message}", sendAttempt, ex.Message.Split('\n')[0]);
                }

                if (await WaitForSubmissionAsync(page, composer, promptText, initialUserCount, initialAssistantCount,
                        initialLatestReplyId, useReplyIds, conversationUrl, clientCancellation))
                    break;

                if (sendAttempt >= MaxSendAttempts)
                    throw new InvalidOperationException($"M365 did not accept the message after {MaxSendAttempts} send attempts; nothing was sent.");

                _logger.LogWarning("Send attempt {Attempt} did not take effect (prompt still in the composer); retrying.", sendAttempt);
                var (retrySend, _, emptied, submitted) = await WaitForEnabledSendAsync();
                if (emptied || submitted)
                    break; // the composer emptied late, or Enter submitted: the message went out
                sendButton = retrySend ?? sendButton;
            }
        }
        catch
        {
            // Nothing was sent: leave an empty composer, or every later request fails readiness ("already contains text")
            await ClearComposerQuietlyAsync(composer);
            throw;
        }

        onSendDispatched?.Invoke();
        _logger.LogInformation("Prompt sent to browser UI. Observing response...");

        // 6. Watch answer with exponential backoff bounded by monotonic deadline
        var currentIntervalMs = 500;
        var maxIntervalMs = onProgress != null ? MaxStreamingPollIntervalMs : MaxPollIntervalMs;
        var generationSeen = false;
        string? lastObservedText = null;
        var stableCount = 0;
        var observed = "no poll completed";

        TimeoutException AnswerTimeout() => new(
            $"Response timeout of {timeout.TotalMinutes} minutes exceeded waiting for assistant answer (last observed: {observed}).");

        while (true)
        {
            var remaining = timeout - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
                throw AnswerTimeout();

            if (page.IsClosed)
                throw new InvalidOperationException("Browser page closed while waiting for answer.");

            if (page.Url != conversationUrl)
            {
                // Allowed: query-only rewrites (M365 drops redirfrom=... params after Send) and the first turn of a
                // new chat navigating /chat -> /chat/conversation/{id}
                if (!IsSamePage(conversationUrl, page.Url) && !IsNewConversationNavigation(conversationUrl, page.Url))
                    throw new InvalidOperationException(
                        $"Conversation or page URL changed during generation ({conversationUrl} -> {page.Url}).");

                conversationUrl = page.Url;
            }

            var usageLimit = await FindUsageLimitNoticeAsync(page);
            if (usageLimit != null)
                throw new UpstreamUiException($"Copilot usage limit reached: {usageLimit}");

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
            var latestReplyId = useReplyIds ? await GetLatestReplyIdAsync(page) : null;
            var hasNewReply = useReplyIds
                ? currentAssistantCount > 0 && latestReplyId != initialLatestReplyId
                : currentAssistantCount > initialAssistantCount;

            // M365 marks the newest reply aria-hidden="true" while writing it and "false" once finished.
            // Counts as generation evidence when a short reply starts and ends between two polls.
            if (hasNewReply && !generationSeen && await IsLatestReplyFinalizedAsync(page))
                generationSeen = true;

            observed = $"replies {initialAssistantCount}->{currentAssistantCount}, newReply={hasNewReply}, generationSeen={generationSeen}, " +
                       $"stop={isGenerating}, send={(currentSendBtn == null ? "absent" : isSendEnabled ? "enabled" : "disabled")}, " +
                       $"composerEmpty={isComposerEmpty}, textLength={lastObservedText?.Length ?? 0}";

            if (hasNewReply)
            {
                var latestText = await GetLatestAssistantMessageTextAsync(page);
                if (!string.IsNullOrWhiteSpace(latestText))
                {
                    if (latestText != lastObservedText)
                    {
                        lastObservedText = latestText;
                        currentIntervalMs = 500; // Reset backoff when text grows
                        stableCount = 0;

                        if (onProgress != null)
                            await onProgress(latestText);
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
                throw AnswerTimeout();

            var delayMs = (int)Math.Min((double)currentIntervalMs, remaining.TotalMilliseconds);
            if (delayMs <= 0)
                throw AnswerTimeout();

            try
            {
                await Task.Delay(delayMs, clientCancellation);
            }
            catch (OperationCanceledException) when (clientCancellation.IsCancellationRequested)
            {
                _logger.LogWarning("Client cancelled request wait after submission. Observing browser until turn completes within remaining budget.");
                await Task.Delay(delayMs, CancellationToken.None);
            }

            currentIntervalMs = Math.Min(currentIntervalMs * 2, maxIntervalMs);
        }
    }

    public async Task StartNewChatAsync(IPage page, CancellationToken cancellationToken)
    {
        var startedInConversation = IsConversationUrl(page.Url);
        var (newChat, _) = await FindFirstElementAsync(page, GetNewChatSelectors(), 2000);
        if (newChat != null)
        {
            await newChat.ClickAsync(new LocatorClickOptions { Timeout = (float)NewChatWait.TotalMilliseconds });
        }
        else
        {
            // Sidebar collapsed or control renamed: open the chat landing page on the same host
            if (!Uri.TryCreate(page.Url, UriKind.Absolute, out var current) ||
                (current.Scheme != Uri.UriSchemeHttp && current.Scheme != Uri.UriSchemeHttps))
                throw new InvalidOperationException($"Cannot start a new chat from '{page.Url}'.");

            _logger.LogWarning("New chat control not found. Navigating to {Origin}/chat.", current.GetLeftPart(UriPartial.Authority));
            await page.GotoAsync(current.GetLeftPart(UriPartial.Authority) + "/chat",
                new PageGotoOptions { Timeout = (float)NewChatWait.TotalMilliseconds });
        }

        // Ready when the previous conversation is gone, the composer is back, and the URL has settled.
        // Starting from /chat/conversation/{id}, the URL must also have left it: the old conversation can briefly
        // render zero replies while the click is still being handled.
        var stopwatch = Stopwatch.StartNew();
        string? previousUrl = null;
        while (stopwatch.Elapsed < NewChatWait)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var url = page.Url;
            if (url == previousUrl && !(startedInConversation && IsConversationUrl(url)) &&
                await GetAssistantMessageCountAsync(page) == 0)
            {
                var (composer, _) = await FindFirstElementAsync(page, GetComposerSelectors(), 500);
                if (composer != null && string.IsNullOrWhiteSpace(await GetComposerTextAsync(composer)))
                {
                    _logger.LogInformation("Started a new chat at {Url}.", url);
                    return;
                }
            }

            previousUrl = url;
            await Task.Delay(250, cancellationToken);
        }

        throw new InvalidOperationException("New chat did not open within 20s; the previous conversation is still shown.");
    }

    private IEnumerable<string> GetNewChatSelectors() =>
        !string.IsNullOrWhiteSpace(_chatOptions.NewChatSelector)
            ? new[] { _chatOptions.NewChatSelector }
            : DefaultNewChatSelectors;

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

    // On the real M365 page only markdown-reply is the answer: broader defaults also match the message container
    // (hidden "Copilot said:" heading, "Gathering details…" placeholder) before markdown-reply exists
    private async Task<IEnumerable<string>> GetAssistantMessageSelectorsAsync(IPage page)
    {
        if (!string.IsNullOrWhiteSpace(_chatOptions.AssistantMessageSelector))
            return new[] { _chatOptions.AssistantMessageSelector };

        try
        {
            if (await page.Locator(M365ComposerSelector).CountAsync() > 0)
                return new[] { M365ReplySelector };
        }
        catch
        {
            // Fall back to the default list
        }

        return DefaultAssistantMessageSelectors;
    }

    private IEnumerable<string> GetErrorBannerSelectors() => DefaultErrorBannerSelectors;

    private static bool IsConversationUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.AbsolutePath.Contains("/conversation/", StringComparison.OrdinalIgnoreCase);

    private static bool IsSamePage(string fromUrl, string toUrl) =>
        Uri.TryCreate(fromUrl, UriKind.Absolute, out var from) &&
        Uri.TryCreate(toUrl, UriKind.Absolute, out var to) &&
        string.Equals(from.Host, to.Host, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(from.AbsolutePath.TrimEnd('/'), to.AbsolutePath.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

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
    private static string NormalizeComposerText(string? text) => StripInvisible(text ?? string.Empty).Trim();

    // U+200B/U+200C/U+200D (zero-width) and U+FEFF (BOM): the editor drops or appends them
    private static string StripInvisible(string text) =>
        new string(text.Where(c => c is not ('​' or '‌' or '‍' or '﻿')).ToArray());

    private async Task ClearComposerQuietlyAsync(ILocator composer)
    {
        try
        {
            // Only when text was left behind; a disabled or empty composer needs nothing and must not stall the error
            if (!string.IsNullOrWhiteSpace(await GetComposerTextAsync(composer, 500)))
                await composer.FillAsync(string.Empty, new LocatorFillOptions { Timeout = 2000 });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not clear the composer after a failed submission attempt.");
        }
    }

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

    // Accepted unless the composer still holds exactly the prompt with no other sign of submission. Other signs:
    // Stop shown, a new user message or reply, an error/limit banner (the observe loop reports it), another conversation
    private async Task<bool> WaitForSubmissionAsync(
        IPage page, ILocator composer, string promptText, int initialUserCount, int initialAssistantCount,
        string? initialLatestReplyId, bool useReplyIds, string conversationUrl, CancellationToken cancellation)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < SendConfirmWait)
        {
            if (page.IsClosed)
                return false;

            try
            {
                if (!string.Equals(await GetComposerTextAsync(composer, 300), promptText.Trim(), StringComparison.Ordinal))
                    return true; // emptied (or changed): the text left the composer

                var (stop, _) = await FindFirstElementAsync(page, GetStopGeneratingSelectors(), 50);
                if (stop != null)
                    return true;

                if (await GetUserMessageCountAsync(page) > initialUserCount)
                    return true;

                var replies = await GetAssistantMessageCountAsync(page);
                if (useReplyIds ? replies > 0 && await GetLatestReplyIdAsync(page) != initialLatestReplyId : replies > initialAssistantCount)
                    return true;

                var (banner, _) = await FindFirstElementAsync(page, GetErrorBannerSelectors(), 50);
                if (banner != null || await FindUsageLimitNoticeAsync(page) != null)
                    return true;

                if (!IsSamePage(conversationUrl, page.Url))
                    return true;
            }
            catch (PlaywrightException)
            {
                // Page re-rendering; check again
            }

            await Task.Delay(200, cancellation);
        }

        return false;
    }

    // Click into the composer to focus it, put the caret at the end (a click lands mid-text, where Enter would only
    // insert a line break and change the text), then press Enter. False when the composer could not be focused.
    private async Task<bool> PressEnterInComposerAsync(ILocator composer, float budgetMs)
    {
        try
        {
            var timeout = Math.Min(5000, budgetMs);
            await composer.ClickAsync(new LocatorClickOptions { Timeout = timeout });
            await composer.EvaluateAsync(@"el => {
                if (typeof el.setSelectionRange === 'function') { el.setSelectionRange(el.value.length, el.value.length); return; }
                const range = document.createRange();
                range.selectNodeContents(el);
                range.collapse(false);
                const selection = window.getSelection();
                selection.removeAllRanges();
                selection.addRange(range);
            }");
            await composer.PressAsync("Enter", new LocatorPressOptions { Timeout = timeout });
            return true;
        }
        catch (Exception ex) when (ex is PlaywrightException || ex is TimeoutException)
        {
            _logger.LogWarning("Could not focus the composer and press Enter: {Message}", ex.Message.Split('\n')[0]);
            return false;
        }
    }

    private async Task<int> GetUserMessageCountAsync(IPage page)
    {
        foreach (var sel in DefaultUserMessageSelectors)
        {
            try
            {
                var count = await page.Locator(sel).CountAsync();
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

    // data-message-id of the newest M365 reply; "" while that reply is still being written (no id yet); null when
    // the page has no replies or does not expose reply ids (then reply counting is used)
    private static async Task<string?> GetLatestReplyIdAsync(IPage page)
    {
        try
        {
            return await page.EvaluateAsync<string?>(@"() => {
                const replies = document.querySelectorAll(""[data-testid='markdown-reply']"");
                if (!replies.length) return null;
                const id = replies[replies.length - 1].getAttribute('data-message-id');
                return id === null ? '' : id;
            }");
        }
        catch
        {
            return null;
        }
    }

    private async Task<int> GetAssistantMessageCountAsync(IPage page)
    {
        foreach (var sel in await GetAssistantMessageSelectorsAsync(page))
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

    // Personal plans: "You've reached your daily limit. Get more usage now or check back at 2:00am", shown as a
    // status banner (not role=alert) while every prompt gets the same canned reply
    private static async Task<string?> FindUsageLimitNoticeAsync(IPage page)
    {
        try
        {
            return await page.EvaluateAsync<string?>(@"() => {
                for (const el of document.querySelectorAll(""[role='status'], [role='alert']"")) {
                    const text = (el.innerText || '').trim();
                    if (/reached your (daily|usage) limit/i.test(text)) return text.replace(/\s+/g, ' ');
                }
                return null;
            }");
        }
        catch
        {
            return null;
        }
    }

    private static async Task<bool> IsLatestReplyFinalizedAsync(IPage page)
    {
        try
        {
            return await page.EvaluateAsync<bool>(@"() => {
                const replies = document.querySelectorAll(""[data-testid='markdown-reply']"");
                return replies.length > 0 && replies[replies.length - 1].getAttribute('aria-hidden') === 'false';
            }");
        }
        catch
        {
            return false;
        }
    }

    private async Task<string> GetLatestAssistantMessageTextAsync(IPage page)
    {
        foreach (var sel in await GetAssistantMessageSelectorsAsync(page))
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
                        // M365 renders code as Fluent components, not <pre>: a language label (unknown languages show as
                        // 'Shell'), an 'isn't fully supported' notice, buttons, and a role=textbox 'Code editor'. Code text is read
                        // from the live element, where innerText keeps line breaks, then the clone's block becomes a fence.
                        var extracted = await last.EvaluateAsync<string>(@"el => {
                            const codeBlockSelector = ""[role='group'][aria-label='Code Preview'], .scriptor-component-code-block"";
                            const outermost = root => [...root.querySelectorAll(codeBlockSelector)]
                                .filter(b => !b.parentElement.closest(codeBlockSelector));
                            const fences = outermost(el).map(block => {
                                const label = block.querySelector(""[data-testid='one-copilot-code-identity']"");
                                const lang = (label ? label.innerText : '').trim().toLowerCase().replace(/[^\w-]/g, '');
                                const editor = block.querySelector(""[role='textbox'][aria-label='Code editor']"");
                                const code = editor ? editor.innerText : '';
                                return '\n```' + lang + '\n' + code.replace(/ /g, ' ').trimEnd() + '\n```\n';
                            });
                            const clone = el.cloneNode(true);
                            outermost(clone).forEach((block, i) => block.replaceWith(document.createTextNode(fences[i] ?? '')));
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

                    // Same selector that produced the count, even when the newest reply is still empty:
                    // broader selectors match the whole message, including its hidden "Copilot said:" heading
                    return (await last.InnerTextAsync()).Trim();
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
