namespace BrowserCodingAgent.Api.Providers;

// Code block rendering on a site: the container replaced by a fence, its language label, the element holding the code.
// GenericLabels are header texts that name no language ("Code snippet").
public sealed record CodeBlockMarkup(string Container, string Label, string Code, string[] GenericLabels);

// What differs between chat web apps; waiting, retrying, send confirmation and parsing are shared (M365ChatAdapter).
// Selected by Chat:Provider. Selectors come from the live pages.
public sealed record ChatSite
{
    public required string Name { get; init; }
    public required string DisplayName { get; init; }
    public required string StartUrl { get; init; }
    public required string ModelAlias { get; init; }

    // Present only on the real site; then only ReplySelector counts as an answer (broader fallbacks match headings)
    public required string ComposerSelector { get; init; }
    public required string[] ComposerSelectors { get; init; }
    public required string[] SendSelectors { get; init; }
    public required string[] StopSelectors { get; init; }
    public required string[] UserMessageSelectors { get; init; }

    public required string ReplySelector { get; init; }
    public required string ReplyIdAttribute { get; init; }

    // JS: reply element => true once the reply is complete (generation evidence for fast replies)
    public required string ReplyFinishedScript { get; init; }

    // JS: composer element => its text, or null to use value/innerText
    public string? ComposerReadScript { get; init; }

    public required CodeBlockMarkup CodeBlocks { get; init; }

    // Regex on the URL path of a saved conversation (the landing/new-chat page does not match)
    public required string ConversationPathPattern { get; init; }

    // JS: () => true when the page is usable but signed out (Gemini works anonymously)
    public string? SignedOutScript { get; init; }

    // Regex on a whole finished reply that is the site's own failure message, not an answer (reported as upstream error)
    public string? ErrorReplyPattern { get; init; }

    // Google sign-in rejects browsers launched with --enable-automation
    public bool LaunchWithoutAutomationFlag { get; init; }

    // Default browser profile folder under ~/.browser-coding-agent/profiles; M365 keeps the original <channel> name
    public string ProfileName(string channel) => Name == M365.Name ? channel : $"{Name}-{channel}";

    public static readonly ChatSite M365 = new()
    {
        Name = "m365",
        DisplayName = "Microsoft 365 Copilot",
        StartUrl = "https://m365.cloud.microsoft/chat",
        ModelAlias = "m365-active",
        ComposerSelector = "#m365-chat-editor-target-element",
        ComposerSelectors = new[] { "#m365-chat-editor-target-element", "[contenteditable='true'][aria-label='Message Copilot']" },
        // The generic lists start with M365's labels
        SendSelectors = Array.Empty<string>(),
        StopSelectors = Array.Empty<string>(),
        UserMessageSelectors = new[] { "[data-testid='chatQuestion']" },
        ReplySelector = "[data-testid='markdown-reply']",
        ReplyIdAttribute = "data-message-id",
        // aria-hidden="true" while the reply is written, "false" once finished
        ReplyFinishedScript = "r => r.getAttribute('aria-hidden') === 'false'",
        // Fluent components: unknown languages are labelled "Shell"; code sits in a role=textbox "Code editor"
        CodeBlocks = new CodeBlockMarkup(
            "[role='group'][aria-label='Code Preview'], .scriptor-component-code-block",
            "[data-testid='one-copilot-code-identity']",
            "[role='textbox'][aria-label='Code editor']",
            Array.Empty<string>()),
        ConversationPathPattern = "/conversation/"
    };

    public static readonly ChatSite Gemini = new()
    {
        Name = "gemini",
        DisplayName = "Gemini",
        StartUrl = "https://gemini.google.com/app",
        ModelAlias = "gemini-active",
        ComposerSelector = "rich-textarea .ql-editor",
        ComposerSelectors = new[] { "rich-textarea .ql-editor", "[contenteditable='true'][aria-label='Enter a prompt for Gemini']" },
        SendSelectors = new[] { "button[aria-label='Send message']" },
        StopSelectors = new[] { "button[aria-label='Stop response']" },
        UserMessageSelectors = new[] { "user-query" },
        // The hidden "Gemini said" heading sits outside message-content
        ReplySelector = "model-response message-content",
        ReplyIdAttribute = "id",
        ReplyFinishedScript = "r => !!r.closest('model-response')?.querySelector('.response-footer.complete')",
        // Quill: one <p> per line, so innerText doubles line breaks; join the lines instead
        ComposerReadScript = @"el => el.classList.contains('ql-editor')
            ? [...el.children].filter(c => !c.classList.contains('ql-clipboard')).map(c => c.textContent).join('\n')
            : null",
        CodeBlocks = new CodeBlockMarkup(
            "code-block",
            ".code-block-decoration > span",
            "code[data-test-id='code-content'], pre code, pre",
            new[] { "code snippet" }),
        ConversationPathPattern = "^/app/[^/]+",
        LaunchWithoutAutomationFlag = true,
        // Shown as a normal reply when the backend request fails
        ErrorReplyPattern = @"^(I seem to be encountering an error|Something went wrong)[^\n]{0,120}$",
        SignedOutScript = @"() => [...document.querySelectorAll('a, button')].some(e =>
            (e.offsetWidth || e.offsetHeight) && /^sign in$/i.test((e.innerText || e.getAttribute('aria-label') || '').trim()))"
    };

    public static readonly IReadOnlyList<ChatSite> All = new[] { M365, Gemini };

    public static ChatSite Get(string? name) =>
        All.FirstOrDefault(s => string.Equals(s.Name, string.IsNullOrWhiteSpace(name) ? M365.Name : name.Trim(), StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException($"Unknown Chat:Provider '{name}'. Supported: {string.Join(", ", All.Select(s => s.Name))}.");
}
