using BrowserCodingAgent.Api.Contracts;
using BrowserCodingAgent.Api.Services;

namespace BrowserCodingAgent.Tests;

public class ConversationMappingTests
{
    private static List<ChatMessageRequest?> Messages(params (string Role, string Text)[] items) =>
        items.Select(i => (ChatMessageRequest?)new ChatMessageRequest(i.Role, i.Text)).ToList();

    [Fact]
    public void FirstTurn_SingleUserMessage_SendsTextAsIs()
    {
        var (turn, error) = ConversationMapper.Map(Messages(("user", "Hello")));

        Assert.Null(error);
        Assert.True(turn!.IsFirstTurn);
        Assert.Equal("Hello", turn.Prompt);
    }

    [Fact]
    public void FirstTurn_BundlesSystemPromptBeforeUserMessage_WithoutTags()
    {
        var (turn, error) = ConversationMapper.Map(Messages(
            ("system", "Rule one."),
            ("developer", "Rule two."),
            ("user", "Hello")));

        Assert.Null(error);
        Assert.True(turn!.IsFirstTurn);
        Assert.Equal("Rule one.\n\nRule two.\n\nHello", turn.Prompt);
    }

    [Fact]
    public void LaterTurn_SendsOnlyMessagesAfterLastAssistant()
    {
        var (turn, error) = ConversationMapper.Map(Messages(
            ("system", "Rule one."),
            ("user", "First"),
            ("assistant", "Reply one"),
            ("user", "Second")));

        Assert.Null(error);
        Assert.False(turn!.IsFirstTurn);
        Assert.Equal("Second", turn.Prompt);
    }

    [Fact]
    public void LaterTurn_ToolResultsAndUserMessage_AreJoinedWithoutTags()
    {
        var (turn, error) = ConversationMapper.Map(Messages(
            ("user", "Read the file"),
            ("assistant", "Calling read_file"),
            ("tool", "file contents"),
            ("user", "Now summarize")));

        Assert.Null(error);
        Assert.Equal("Tool result:\nfile contents\n\nNow summarize", turn!.Prompt);
    }

    [Fact]
    public void EndsWithAssistant_IsRejected()
    {
        var (turn, error) = ConversationMapper.Map(Messages(("user", "Hello"), ("assistant", "Hi")));

        Assert.Null(turn);
        Assert.Equal("no_new_message", error!.Code);
    }

    [Fact]
    public void UnknownRole_IsRejected()
    {
        var (_, error) = ConversationMapper.Map(Messages(("narrator", "Once upon a time")));

        Assert.Equal("invalid_role", error!.Code);
        Assert.Equal("messages[0].role", error.Param);
    }

    [Fact]
    public void EmptyUserMessage_IsRejected()
    {
        var (_, error) = ConversationMapper.Map(Messages(("user", "   ")));

        Assert.Equal("empty_content", error!.Code);
    }
}

public class StreamingTextEmitterTests
{
    [Fact]
    public void AppendOnlySnapshots_EmitDeltas_HoldingBackTheGrowingWord()
    {
        var emitter = new StreamingTextEmitter();

        Assert.Equal(string.Empty, emitter.Next("Hel"));
        Assert.Equal("Hello ", emitter.Next("Hello wor"));
        Assert.Equal("world", emitter.Complete("Hello world"));
        Assert.False(emitter.FinalMismatch);
    }

    [Fact]
    public void RewrittenSnapshot_EmitsNothingUntilItExtendsSentText()
    {
        var emitter = new StreamingTextEmitter();

        Assert.Equal("Hello ", emitter.Next("Hello wor"));
        Assert.Equal(string.Empty, emitter.Next("Hello, wor"));
        Assert.Equal("world ", emitter.Next("Hello world again"));
    }

    [Fact]
    public void UnfinishedCodeBlock_HoldsBackClosingFence()
    {
        var emitter = new StreamingTextEmitter();

        Assert.Equal("Code:\n```csharp\nvar ", emitter.Next("Code:\n```csharp\nvar x\n```"));
        Assert.Equal("x = 1;", emitter.Next("Code:\n```csharp\nvar x = 1;\n```"));
        Assert.Equal("\n```", emitter.Complete("Code:\n```csharp\nvar x = 1;\n```"));
    }

    [Fact]
    public void FinalTextNotExtendingStream_IsReportedAsMismatch()
    {
        var emitter = new StreamingTextEmitter();

        Assert.Equal("Draft answer.", emitter.Next("Draft answer."));
        Assert.Equal(string.Empty, emitter.Complete("Final answer"));
        Assert.True(emitter.FinalMismatch);
    }

    [Fact]
    public void ReplacementWordSplitAcrossSnapshots_IsMappedBackWhole()
    {
        var replacements = new PromptReplacements(new[] { new ReplacementRule("Cline", "Codewright", Reverse: true) }, "test");
        var emitter = new StreamingTextEmitter();

        Assert.Equal("Hi, I am ", emitter.Next(replacements.ApplyIncoming("Hi, I am Codewr")));
        Assert.Equal("Cline.", emitter.Next(replacements.ApplyIncoming("Hi, I am Codewright.")));
        Assert.Equal("Hi, I am Cline.", emitter.Emitted);
    }
}

public class PromptReplacementsTests
{
    // The default file shipped with the app (copied to the test output via the project reference)
    private static PromptReplacements LoadDefault() => PromptReplacements.Load(
        Path.Combine(AppContext.BaseDirectory, PromptReplacements.FileName),
        AppContext.BaseDirectory,
        Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

    [Fact]
    public void DefaultFile_RewritesClinePersonaAndEnvBlock()
    {
        var output = LoadDefault().ApplyOutgoing(
            "You are Cline, an AI coding agent.\n\nEnvironment you are running in:\n<env>\n1. Platform: darwin\n</env>\n{{CLINE_RULES}}");

        Assert.Contains("You help the user with coding tasks through the Codewright application on their computer.", output);
        Assert.Contains("The user's computer (the application runs vexlo pings here):", output);
        Assert.Contains("1. Platform: darwin", output);
        Assert.DoesNotContain("<env>", output);
        Assert.DoesNotContain("</env>", output);
        Assert.DoesNotContain("Cline", output);
    }

    [Fact]
    public void DefaultFile_TurnsUserInputWrapperIntoModeLine()
    {
        var output = LoadDefault().ApplyOutgoing("<user_input mode=\"plan\">\nFix the login bug\n</user_input>\n<mode_notice>switched</mode_notice>");

        Assert.Equal("Mode: plan\nFix the login bug\nswitched", output);
    }

    [Fact]
    public void WholeWordMatching_LeavesWordsContainingTheNameAlone()
    {
        var output = LoadDefault().ApplyOutgoing("decline incline Cline's cline-rules .clinerules");

        Assert.Equal("decline incline Codewright's codewright-rules .clinerules", output);
    }

    [Fact]
    public void ReverseRules_MapRepliesBack()
    {
        var output = LoadDefault().ApplyIncoming("Codewright here; codewright config; CODEWRIGHT; Codewrights stay");

        Assert.Equal("Cline here; cline config; CLINE; Codewrights stay", output);
    }

    [Fact]
    public void DefaultFile_HidesToolVocabulary_AppendsTextOnlyInstruction_AndParsesAliasedReply()
    {
        var replacements = LoadDefault();
        var specs = ToolCalling.ParseDefinitions(System.Text.Json.JsonDocument.Parse(
            """[{"type":"function","function":{"name":"read_files","description":"Read files.","parameters":{"type":"object"}}}]""").RootElement);

        var prompt = replacements.PrepareOutgoing(
            "You are Cline. Use the tools available to you.\n\n" + ToolCalling.BuildInstructions(specs, null, firstTurn: true));
        var beforeAppend = prompt[..prompt.LastIndexOf("\n\n", StringComparison.Ordinal)];

        Assert.DoesNotMatch(@"(?i)(?<!\w)tool(s|_call|call| call)?(?!\w)", beforeAppend);
        Assert.Contains("```vexlo_ping", beforeAppend);
        Assert.Contains("- open_documents: Read files.", beforeAppend);
        Assert.EndsWith(replacements.AppendToPrompt!, prompt);
        Assert.Contains("built-in tools", replacements.AppendToPrompt);

        var reply = replacements.ApplyIncoming(
            "Reading it.\n```vexlo_ping\n{\"name\": \"open_documents\", \"arguments\": {\"files\": []}}\n```");
        var (text, calls) = ToolCalling.ParseReply(reply, new[] { "read_files" });
        Assert.Equal("Reading it.", text.Trim());
        Assert.Equal("read_files", Assert.Single(calls).Function.Name);
    }

    [Fact]
    public void RegexRule_WithReverse_IsRejected()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new PromptReplacements(new[] { new ReplacementRule("a+", "b", Regex: true, Reverse: true) }, "test.json"));

        Assert.Contains("replacements[0]", ex.Message);
    }

    [Fact]
    public void InvalidRegex_IsReportedWithIndex()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new PromptReplacements(new[] { new ReplacementRule("ok", "fine"), new ReplacementRule("([", "x", Regex: true) }, "test.json"));

        Assert.Contains("replacements[1].find is not a valid regex", ex.Message);
    }

    [Fact]
    public void MissingConfiguredFile_FailsWithPath()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            PromptReplacements.Load("/nonexistent/replacements.json", AppContext.BaseDirectory,
                Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance));

        Assert.Contains(Path.GetFullPath("/nonexistent/replacements.json"), ex.Message);
    }
}

public class PromptChunkerTests
{
    private static string Lines(int count) => string.Concat(Enumerable.Range(0, count).Select(i => $"line {i:D5} of the tool output\n"));

    [Fact]
    public void Split_ReassemblesExactly_AndCutsAtLineBreaks()
    {
        var text = Lines(1000);

        var chunks = PromptChunker.Split(text, 6700);

        Assert.Equal(text, string.Concat(chunks));
        Assert.All(chunks, c => Assert.True(c.Length <= 6700));
        Assert.All(chunks.SkipLast(1), c => Assert.EndsWith("\n", c));
    }

    [Fact]
    public void Split_WithoutLineBreaks_CutsAtWindow()
    {
        var text = new string('x', 15000);

        var chunks = PromptChunker.Split(text, 6700);

        Assert.Equal(new[] { 6700, 6700, 1600 }, chunks.Select(c => c.Length));
    }

    [Fact]
    public void Wrap_NumbersParts_KeepsEachUnderLimit_AndAsksForTheAnswerLast()
    {
        var parts = PromptChunker.Wrap(Lines(1000), 7000);

        Assert.True(parts.Count > 1);
        Assert.All(parts, p => Assert.True(p.Length <= 7000, $"part has {p.Length} chars"));
        Assert.StartsWith($"Part 1 of {parts.Count} of one long message. Do not answer or act on it yet; reply only with: received 1/{parts.Count}", parts[0]);
        Assert.StartsWith($"Part {parts.Count} of {parts.Count}. The message is complete.", parts[^1]);
    }

    [Fact]
    public void Wrap_ShortPrompt_IsSentAsIs()
    {
        Assert.Equal(new[] { "short" }, PromptChunker.Wrap("short", 7000));
    }
}
