namespace BrowserCodingAgent.Api.Services;

// Splits a prompt that exceeds the composer limit into parts sent as consecutive messages in the same chat.
// Parts 1..n-1 ask Copilot to only acknowledge; part n asks it to answer the whole message, so only the last
// reply is the answer. Each part is a complete composer fill, verified on its own.
public static class PromptChunker
{
    public const int MinChunkChars = 1000;
    public const int DefaultLimit = 7000;

    // Room for the wrapper text around each part ("Part 12 of 34 ..."), so a wrapped part stays under the limit
    private const int WrapperReserve = 300;

    public static string Intermediate(int part, int total, string chunk) =>
        $"Part {part} of {total} of one long message. Do not answer or act on it yet; reply only with: received {part}/{total}\n\n{chunk}";

    public static string Final(int total, string chunk) =>
        $"Part {total} of {total}. The message is complete. Treat parts 1 to {total} as one message and respond to it now.\n\n{chunk}";

    public static IReadOnlyList<string> Wrap(string prompt, int limit)
    {
        var chunks = Split(prompt, Math.Max(MinChunkChars, limit) - WrapperReserve);
        if (chunks.Count == 1)
            return chunks;

        return chunks
            .Select((chunk, i) => i < chunks.Count - 1 ? Intermediate(i + 1, chunks.Count, chunk) : Final(chunks.Count, chunk))
            .ToList();
    }

    // Cuts at the last line break in the final fifth of each window, else at the window end (never inside a surrogate pair)
    public static List<string> Split(string text, int size)
    {
        var chunks = new List<string>();
        var start = 0;
        while (text.Length - start > size)
        {
            var end = start + size;
            var newline = text.LastIndexOf('\n', end - 1, size - size * 4 / 5);
            if (newline > start)
                end = newline + 1;
            else if (char.IsHighSurrogate(text[end - 1]))
                end--;

            chunks.Add(text[start..end]);
            start = end;
        }

        chunks.Add(text[start..]);
        return chunks;
    }
}
