using System.ComponentModel.DataAnnotations;
using BrowserCodingAgent.Api.Providers;
using BrowserCodingAgent.Api.Services;

namespace BrowserCodingAgent.Api.Configuration;

public class BrowserOptions
{
    public const string SectionName = "Browser";

    [Required(AllowEmptyStrings = false)]
    public string Channel { get; set; } = "chrome";

    public string? UserDataDirectory { get; set; }

    // Unset = the provider's chat page (ChatSite.StartUrl)
    public string? StartUrl { get; set; }

    public bool Headless { get; set; } = false;
    public bool AutoStart { get; set; } = true;

    public void Validate()
    {
        var ch = Channel?.Trim().ToLowerInvariant();
        if (ch != "chrome" && ch != "msedge")
            throw new InvalidOperationException($"Invalid Browser:Channel '{Channel}'. Must be 'chrome' or 'msedge'.");

        if (StartUrl != null && (!Uri.TryCreate(StartUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)))
        {
            throw new InvalidOperationException($"Invalid Browser:StartUrl '{StartUrl}'. Must be an absolute HTTP or HTTPS URL.");
        }
    }

    public string ResolveStartUrl(ChatSite site) => string.IsNullOrWhiteSpace(StartUrl) ? site.StartUrl : StartUrl;

    public string ResolveUserDataDirectory(string contentRoot, ChatSite site)
    {
        var channelName = string.IsNullOrWhiteSpace(Channel) ? "chrome" : Channel.Trim().ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(UserDataDirectory))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, ".browser-coding-agent", "profiles", site.ProfileName(channelName));
        }

        var path = UserDataDirectory;
        if (path.StartsWith("~"))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            path = Path.Combine(home, path.TrimStart('~', '/', '\\'));
        }
        else if (!Path.IsPathRooted(path))
        {
            path = Path.Combine(contentRoot, path);
        }

        return Path.GetFullPath(path);
    }
}

public class ChatOptions
{
    public const string SectionName = "Chat";

    // Chat site: m365 (default) or gemini
    public string? Provider { get; set; }

    private string? _modelAlias;

    // Unset = the provider's alias (m365-active, gemini-active)
    public string ModelAlias
    {
        get => string.IsNullOrWhiteSpace(_modelAlias) ? ChatSite.Get(Provider).ModelAlias : _modelAlias;
        set => _modelAlias = value;
    }

    [Range(1, 120)]
    public int ResponseTimeoutMinutes { get; set; } = 10;

    public string? ComposerSelector { get; set; }
    public string? SendButtonSelector { get; set; }
    public string? AssistantMessageSelector { get; set; }
    public string? StopGeneratingSelector { get; set; }
    public string? NewChatSelector { get; set; }

    // Longest prompt sent in one message; longer prompts go in parts. 0 = only when the composer refuses a prompt
    // (it then chunks under the observed limit). Accounts with an 8000-character composer: 7000.
    public int MaxPromptChars { get; set; }

    // Wait for Send to enable after filling the composer (M365 needs ~3s; longer on a slow network)
    [Range(5, 300)]
    public int SendEnableSeconds { get; set; } = 30;

    // Prompt/reply replacement rules; default lookup ~/.browser-coding-agent/replacements.json, then the bundled file
    public string? ReplacementsFile { get; set; }

    public void Validate()
    {
        ChatSite.Get(Provider); // throws on an unknown provider

        if (MaxPromptChars != 0 && MaxPromptChars < PromptChunker.MinChunkChars)
            throw new InvalidOperationException($"Chat:MaxPromptChars must be 0 or at least {PromptChunker.MinChunkChars}. Got {MaxPromptChars}.");

        if (SendEnableSeconds < 5 || SendEnableSeconds > 300)
            throw new InvalidOperationException($"Chat:SendEnableSeconds must be between 5 and 300. Got {SendEnableSeconds}.");

        if (ResponseTimeoutMinutes < 1 || ResponseTimeoutMinutes > 120)
            throw new InvalidOperationException($"Chat:ResponseTimeoutMinutes must be between 1 and 120. Got {ResponseTimeoutMinutes}.");
    }
}
