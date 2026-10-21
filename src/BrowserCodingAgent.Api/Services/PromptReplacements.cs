using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace BrowserCodingAgent.Api.Services;

public record ReplacementRule(
    [property: JsonPropertyName("find")] string Find,
    [property: JsonPropertyName("replace")] string Replace,
    [property: JsonPropertyName("regex")] bool Regex = false,
    [property: JsonPropertyName("wholeWord")] bool WholeWord = true,
    [property: JsonPropertyName("reverse")] bool Reverse = false
);

public record ReplacementFile(
    [property: JsonPropertyName("replacements")] List<ReplacementRule>? Replacements,
    [property: JsonPropertyName("appendToPrompt")] string? AppendToPrompt = null
);

// Rewrites prompts before they reach M365 (client names and wrapper tags that trip Copilot's guardrails)
// and maps reversible replacements back in replies.
// Lookup: Chat:ReplacementsFile -> ~/.browser-coding-agent/replacements.json -> replacements.json next to the app.
public sealed class PromptReplacements
{
    public const string FileName = "replacements.json";

    public static readonly PromptReplacements None = new(Array.Empty<ReplacementRule>(), "none");

    private readonly (Regex Pattern, string Replacement)[] _outgoing;
    private readonly (Regex Pattern, string Replacement)[] _incoming;

    public PromptReplacements(IReadOnlyList<ReplacementRule> rules, string source, string? appendToPrompt = null)
    {
        Source = source;
        Count = rules.Count;
        AppendToPrompt = string.IsNullOrWhiteSpace(appendToPrompt) ? null : appendToPrompt.Trim();

        for (var i = 0; i < rules.Count; i++)
        {
            if (string.IsNullOrEmpty(rules[i].Find))
                throw new InvalidOperationException($"{source}: replacements[{i}].find is empty.");
            if (rules[i].Regex && rules[i].Reverse)
                throw new InvalidOperationException($"{source}: replacements[{i}] sets both regex and reverse; reverse needs plain text.");
            if (rules[i].Reverse && string.IsNullOrEmpty(rules[i].Replace))
                throw new InvalidOperationException($"{source}: replacements[{i}] sets reverse with an empty replace.");
        }

        _outgoing = rules
            .Select((r, i) => (Compile(r.Find, r.Regex, r.WholeWord, source, i), r.Regex ? r.Replace ?? "" : Literal(r.Replace)))
            .ToArray();
        _incoming = rules
            .Select((r, i) => (Rule: r, Index: i))
            .Where(x => x.Rule.Reverse)
            .Select(x => (Compile(x.Rule.Replace, false, x.Rule.WholeWord, source, x.Index), Literal(x.Rule.Find)))
            .ToArray();
    }

    public string Source { get; }
    public int Count { get; }
    public string? AppendToPrompt { get; }

    public string ApplyOutgoing(string text) => Apply(text, _outgoing);

    // Full prompt for M365: replacements, then the fixed instruction (appended afterwards so its words stay as written)
    public string PrepareOutgoing(string prompt) =>
        AppendToPrompt == null ? ApplyOutgoing(prompt) : $"{ApplyOutgoing(prompt)}\n\n{AppendToPrompt}";

    public string ApplyIncoming(string text) => Apply(text, _incoming);

    public static PromptReplacements Load(string? configuredPath, string appDirectory, ILogger logger)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var homeFile = Path.Combine(home, ".browser-coding-agent", FileName);
        var bundledFile = Path.Combine(appDirectory, FileName);

        string path;
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var expanded = configuredPath.StartsWith('~') ? Path.Combine(home, configuredPath.TrimStart('~', '/', '\\')) : configuredPath;
            path = Path.GetFullPath(expanded);
            if (!File.Exists(path))
                throw new InvalidOperationException($"Chat:ReplacementsFile '{path}' does not exist.");
        }
        else if (File.Exists(homeFile))
        {
            path = homeFile;
        }
        else if (File.Exists(bundledFile))
        {
            path = bundledFile;
        }
        else
        {
            logger.LogInformation("No {FileName} found; prompts are sent unchanged.", FileName);
            return None;
        }

        ReplacementFile? file;
        try
        {
            file = JsonSerializer.Deserialize<ReplacementFile>(File.ReadAllText(path),
                new JsonSerializerOptions { AllowTrailingCommas = true, ReadCommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"{path} is not valid JSON: {ex.Message}", ex);
        }

        var replacements = new PromptReplacements(file?.Replacements ?? new List<ReplacementRule>(), path, file?.AppendToPrompt);
        logger.LogInformation("Loaded {Count} prompt replacements from {Path} (appendToPrompt: {Append}).",
            replacements.Count, path, replacements.AppendToPrompt == null ? "none" : $"{replacements.AppendToPrompt.Length} chars");
        return replacements;
    }

    private static string Apply(string text, (Regex Pattern, string Replacement)[] rules)
    {
        foreach (var (pattern, replacement) in rules)
            text = pattern.Replace(text, replacement);
        return text;
    }

    private static Regex Compile(string find, bool isRegex, bool wholeWord, string source, int index)
    {
        var body = isRegex ? find : System.Text.RegularExpressions.Regex.Escape(find);
        if (!isRegex && wholeWord)
        {
            if (IsWordChar(find[0]))
                body = @"(?<!\w)" + body;
            if (IsWordChar(find[^1]))
                body += @"(?!\w)";
        }

        try
        {
            return new Regex(body, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException($"{source}: replacements[{index}].find is not a valid regex: {ex.Message}", ex);
        }
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static string Literal(string? replacement) => (replacement ?? "").Replace("$", "$$");
}
