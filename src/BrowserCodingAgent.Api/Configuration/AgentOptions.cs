using System.ComponentModel.DataAnnotations;

namespace BrowserCodingAgent.Api.Configuration;

public class BrowserOptions
{
    public const string SectionName = "Browser";

    [Required(AllowEmptyStrings = false)]
    public string Channel { get; set; } = "chrome";

    public string? UserDataDirectory { get; set; }

    [Required(AllowEmptyStrings = false)]
    public string StartUrl { get; set; } = "https://m365.cloud.microsoft/chat";

    public bool Headless { get; set; } = false;
    public bool AutoStart { get; set; } = true;

    public void Validate()
    {
        var ch = Channel?.Trim().ToLowerInvariant();
        if (ch != "chrome" && ch != "msedge")
            throw new InvalidOperationException($"Invalid Browser:Channel '{Channel}'. Must be 'chrome' or 'msedge'.");

        if (string.IsNullOrWhiteSpace(StartUrl) || !Uri.TryCreate(StartUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException($"Invalid Browser:StartUrl '{StartUrl}'. Must be an absolute HTTP or HTTPS URL.");
        }
    }

    public string ResolveUserDataDirectory(string contentRoot)
    {
        var channelName = string.IsNullOrWhiteSpace(Channel) ? "chrome" : Channel.Trim().ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(UserDataDirectory))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, ".browser-coding-agent", "profiles", channelName);
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

    [Required(AllowEmptyStrings = false)]
    public string ModelAlias { get; set; } = "m365-active";

    [Range(1, 120)]
    public int ResponseTimeoutMinutes { get; set; } = 10;

    public string? ComposerSelector { get; set; }
    public string? SendButtonSelector { get; set; }
    public string? AssistantMessageSelector { get; set; }
    public string? StopGeneratingSelector { get; set; }
    public string? NewChatSelector { get; set; }

    // Prompt/reply replacement rules; default lookup ~/.browser-coding-agent/replacements.json, then the bundled file
    public string? ReplacementsFile { get; set; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ModelAlias))
            throw new InvalidOperationException("Chat:ModelAlias cannot be empty.");

        if (ResponseTimeoutMinutes < 1 || ResponseTimeoutMinutes > 120)
            throw new InvalidOperationException($"Chat:ResponseTimeoutMinutes must be between 1 and 120. Got {ResponseTimeoutMinutes}.");
    }
}
