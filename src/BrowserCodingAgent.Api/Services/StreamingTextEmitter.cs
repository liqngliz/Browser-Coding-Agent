namespace BrowserCodingAgent.Api.Services;

// Turns successive full-text snapshots of the M365 reply into append-only deltas.
// Sent text cannot be retracted, so a snapshot that does not extend it emits nothing.
public class StreamingTextEmitter
{
    private string _emitted = string.Empty;

    public string Emitted => _emitted;

    // True when the completed answer did not extend the streamed text; the client then holds a truncated answer
    public bool FinalMismatch { get; private set; }

    public string Next(string snapshot) => Advance(HoldBackTrailingWord(HoldBackTrailingFence(snapshot))) ?? string.Empty;

    public string Complete(string finalText)
    {
        var delta = Advance(finalText);
        FinalMismatch = delta == null;
        return delta ?? string.Empty;
    }

    private string? Advance(string candidate)
    {
        if (!candidate.StartsWith(_emitted, StringComparison.Ordinal))
            return null;

        var delta = candidate[_emitted.Length..];
        _emitted = candidate;
        return delta;
    }

    // The last word may still be growing, and reverse replacements match whole words only ("Codewr" -> "Codewright" -> "Cline")
    private static string HoldBackTrailingWord(string text)
    {
        var end = text.Length;
        while (end > 0 && (char.IsLetterOrDigit(text[end - 1]) || text[end - 1] == '_'))
            end--;
        return text[..end];
    }

    // DOM extraction closes every <pre> with a ``` line, so an unfinished code block ends in a fence that moves
    private static string HoldBackTrailingFence(string text)
    {
        if (!text.EndsWith("```", StringComparison.Ordinal))
            return text;

        var lastLineStart = text.LastIndexOf('\n');
        return lastLineStart < 0 ? string.Empty : text[..lastLineStart];
    }
}
