using System.Text.Json;
using BrowserCodingAgent.Api.Contracts;
using BrowserCodingAgent.Api.Services;

namespace BrowserCodingAgent.Tests;

public class ToolCallingTests
{
    private static readonly string[] Names = { "read_files", "run_commands" };

    private static JsonElement Tools() => JsonDocument.Parse("""
        [
          {"type":"function","function":{"name":"read_files","description":"Read files.","parameters":{"type":"object","properties":{"files":{"type":"array"}}}}},
          {"type":"function","function":{"name":"run_commands","parameters":{"type":"object"}}}
        ]
        """).RootElement;

    [Fact]
    public void ParseDefinitions_ReadsNameDescriptionAndSchema()
    {
        var specs = ToolCalling.ParseDefinitions(Tools());

        Assert.Equal(new[] { "read_files", "run_commands" }, specs.Select(s => s.Name));
        Assert.Equal("Read files.", specs[0].Description);
        Assert.Contains("\"files\"", specs[0].ParametersJson);
        Assert.Equal("{\"type\":\"object\"}", specs[1].ParametersJson);
    }

    [Fact]
    public void ParseDefinitions_WithoutName_Throws()
    {
        Assert.Throws<FormatException>(() =>
            ToolCalling.ParseDefinitions(JsonDocument.Parse("[{\"type\":\"function\",\"function\":{}}]").RootElement));
    }

    [Theory]
    [InlineData("\"auto\"", true, null)]
    [InlineData("\"none\"", false, null)]
    [InlineData("\"required\"", true, "You must call at least one tool in this reply.")]
    [InlineData("{\"type\":\"function\",\"function\":{\"name\":\"read_files\"}}", true, "You must call the tool read_files in this reply.")]
    public void ParseChoice_MapsToolChoice(string json, bool enabled, string? required)
    {
        var (isEnabled, requiredLine) = ToolCalling.ParseChoice(JsonDocument.Parse(json).RootElement);

        Assert.Equal(enabled, isEnabled);
        Assert.Equal(required, requiredLine);
    }

    [Fact]
    public void BuildInstructions_FirstTurnListsToolsAndFormat_LaterTurnListsNames()
    {
        var specs = ToolCalling.ParseDefinitions(Tools());

        var first = ToolCalling.BuildInstructions(specs, null, firstTurn: true);
        Assert.Contains("```tool_call", first);
        Assert.Contains("- read_files: Read files.", first);
        Assert.Contains("Do not use your own code interpreter", first);
        Assert.DoesNotContain("<", first.Replace("<tool name>", ""));

        Assert.Contains("Never invent other tool names.", first);

        var later = ToolCalling.BuildInstructions(specs, "You must call at least one tool in this reply.", firstTurn: false);
        Assert.Contains("```tool_call\n{\"name\": \"<tool name>\", \"arguments\": {}}\n```", later);
        Assert.Contains("use exactly these with these parameters: read_files(files), run_commands().", later);
        Assert.EndsWith("You must call at least one tool in this reply.", later);
    }

    [Fact]
    public void BuildInstructions_UsesLfOnly()
    {
        var specs = ToolCalling.ParseDefinitions(Tools());

        Assert.DoesNotContain('\r', ToolCalling.BuildInstructions(specs, "You must call at least one tool in this reply.", firstTurn: true));
        Assert.DoesNotContain('\r', ToolCalling.BuildInstructions(specs, null, firstTurn: false));
    }

    [Fact]
    public void ParseReply_ToolCallFence_BecomesToolCallAndLeavesText()
    {
        var (text, calls) = ToolCalling.ParseReply(
            "I'll read it.\n\n```tool_call\n{\"name\": \"read_files\", \"arguments\": {\"files\": [\"/a.cs\"]}}\n```",
            Names);

        Assert.Equal("I'll read it.", text.Trim());
        var call = Assert.Single(calls);
        Assert.Equal("read_files", call.Function.Name);
        Assert.Equal("{\"files\":[\"/a.cs\"]}", call.Function.Arguments);
        Assert.StartsWith("call_", call.Id);
        Assert.Equal("function", call.Type);
    }

    [Fact]
    public void ParseReply_UnlabeledAndJsonFences_AndArraysAreAccepted()
    {
        var (text, calls) = ToolCalling.ParseReply(
            "```\n{\"name\": \"run_commands\", \"arguments\": {\"commands\": [\"ls\"]}}\n```\n" +
            "```json\n[{\"name\": \"read_files\", \"arguments\": \"{\\\"files\\\":[]}\"}, {\"name\": \"run_commands\"}]\n```",
            Names);

        Assert.Equal(string.Empty, text.Trim());
        Assert.Equal(new[] { "run_commands", "read_files", "run_commands" }, calls.Select(c => c.Function.Name));
        Assert.Equal("{\"files\":[]}", calls[1].Function.Arguments);
        Assert.Equal("{}", calls[2].Function.Arguments);
    }

    [Fact]
    public void ParseReply_RelabeledFence_IsAccepted()
    {
        // M365 shows unknown languages as "Shell"; the extraction emits that label
        var (text, calls) = ToolCalling.ParseReply("```shell\n{\"name\": \"read_files\", \"arguments\": {}}\n```", Names);

        Assert.Equal(string.Empty, text.Trim());
        Assert.Equal("read_files", Assert.Single(calls).Function.Name);
    }

    [Fact]
    public void ParseReply_UnknownToolOrOrdinaryCode_StaysText()
    {
        var reply = "```json\n{\"name\": \"delete_everything\", \"arguments\": {}}\n```\n```csharp\nvar x = 1;\n```";

        var (text, calls) = ToolCalling.ParseReply(reply, Names);

        Assert.Empty(calls);
        Assert.Equal(reply, text);
    }

    [Fact]
    public void ParseReply_LenientShapes_AreAccepted()
    {
        // Two objects in one block, trailing comma, a comment, OpenAI "function" wrapper, "parameters" key
        var reply = "```tool_call\n" +
                    "{\"name\": \"read_files\", \"arguments\": {\"files\": [\"/a\"],},}\n" +
                    "// second call\n" +
                    "{\"type\": \"function\", \"function\": {\"name\": \"run_commands\", \"parameters\": {\"commands\": [\"ls\"]}}}\n" +
                    "```";

        var (text, calls) = ToolCalling.ParseReply(reply, Names, out var rejected);

        Assert.Empty(rejected);
        Assert.Equal(string.Empty, text.Trim());
        Assert.Equal(new[] { "read_files", "run_commands" }, calls.Select(c => c.Function.Name));
        Assert.Equal("{\"files\":[\"/a\"]}", calls[0].Function.Arguments);
        Assert.Equal("{\"commands\":[\"ls\"]}", calls[1].Function.Arguments);
    }

    [Fact]
    public void ParseReply_ReportsWhyCallLikeBlocksStayedText()
    {
        var reply = "```tool_call\n{\"name\": \"cmd\", \"arguments\": {}}\n```\n" +
                    "```json\n{\"name\": \"read_files\", \"arguments\": {\n```\n" +
                    "```csharp\nvar x = 1;\n```";

        var (text, calls) = ToolCalling.ParseReply(reply, Names, out var rejected);

        Assert.Empty(calls);
        Assert.Equal(reply, text);
        Assert.Equal(2, rejected.Count);
        Assert.Equal("unknown tool 'cmd'", rejected[0]);
        Assert.StartsWith("invalid JSON", rejected[1]);
    }

    [Fact]
    public void Signature_MarksOptionalParameters()
    {
        var spec = new ToolSpec("editor", null,
            "{\"type\":\"object\",\"properties\":{\"command\":{},\"path\":{},\"old_str\":{}},\"required\":[\"command\",\"path\"]}");

        Assert.Equal("editor(command, path, old_str?)", ToolCalling.Signature(spec));
    }

    [Fact]
    public void ParseReply_BareCallWithoutFence_IsAccepted()
    {
        var (text, calls) = ToolCalling.ParseReply("{\"name\": \"read_files\", \"arguments\": {\"files\": [\"/b.cs\"]}}", Names);

        Assert.Equal(string.Empty, text);
        Assert.Equal("read_files", Assert.Single(calls).Function.Name);
    }

    [Fact]
    public void VisiblePrefix_StopsAtFirstFence()
    {
        Assert.Equal("Reading.\n", ToolCalling.VisiblePrefix("Reading.\n```tool_call\n{\"name\""));
        Assert.Equal("No fences here", ToolCalling.VisiblePrefix("No fences here"));
    }

    [Fact]
    public void Mapper_LabelsToolResultsWithTheCalledToolName()
    {
        var assistant = new ChatMessageRequest("assistant", "Reading.")
        {
            ToolCalls = new List<ToolCall> { new("call_1", "function", new ToolCallFunction("read_files", "{}")) }
        };
        var messages = new List<ChatMessageRequest?>
        {
            new("user", "Read a.cs"),
            assistant,
            new("tool", "class A {}") { ToolCallId = "call_1" },
            new("tool", "") { ToolCallId = "call_unknown" }
        };

        var (turn, error) = ConversationMapper.Map(messages);

        Assert.Null(error);
        Assert.Equal("Result of read_files:\nclass A {}\n\nTool result:\n(no output)", turn!.Prompt);
    }
}
