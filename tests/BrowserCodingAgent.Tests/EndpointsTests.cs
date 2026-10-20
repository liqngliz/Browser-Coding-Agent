using System.Net;
using System.Net.Http.Json;
using System.Text;
using BrowserCodingAgent.Api.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace BrowserCodingAgent.Tests;

public class EndpointsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public EndpointsTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Browser:AutoStart", "false");
            builder.UseSetting("Browser:Headless", "true");
        });
    }

    [Fact]
    public async Task GetHealthLive_Returns200AndLiveStatus()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<HealthLiveResponse>();
        Assert.NotNull(body);
        Assert.Equal("live", body.Status);
    }

    [Fact]
    public async Task GetHealthReady_WhenBrowserNotRunning_Returns503NotReady()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<HealthReadyResponse>();
        Assert.NotNull(body);
        Assert.Equal("not_ready", body.Status);
        Assert.False(string.IsNullOrWhiteSpace(body.Reason));
    }

    [Fact]
    public async Task GetModels_ReturnsConfiguredModelAlias()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/v1/models");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ModelsResponse>();
        Assert.NotNull(body);
        Assert.Equal("list", body.Object);
        Assert.Single(body.Data);
        Assert.Equal("m365-active", body.Data[0].Id);
    }

    [Fact]
    public async Task PostChatCompletions_MessagesContainsNull_Returns400()
    {
        var client = _factory.CreateClient();
        var content = new StringContent(
            "{\"model\":\"m365-active\",\"messages\":[null]}",
            Encoding.UTF8,
            "application/json");

        var response = await client.PostAsync("/v1/chat/completions", content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.NotNull(body);
        Assert.Equal("invalid_messages", body.Error.Code);
    }

    [Fact]
    public async Task PostChatCompletions_TextContentPartArray_Accepted()
    {
        var client = _factory.CreateClient();
        var content = new StringContent(
            "{\"model\":\"m365-active\",\"messages\":[{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"Hello part\"}]}]}",
            Encoding.UTF8,
            "application/json");

        var response = await client.PostAsync("/v1/chat/completions", content);
        // Validated successfully; reaches browser readiness check (503 because browser AutoStart is false)
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.NotNull(body);
        Assert.Equal("browser_not_ready", body.Error.Code);
    }

    [Fact]
    public async Task PostChatCompletions_InvalidContentPartTypeKind_Returns400()
    {
        var client = _factory.CreateClient();
        var content = new StringContent(
            "{\"model\":\"m365-active\",\"messages\":[{\"role\":\"user\",\"content\":[{\"type\":1,\"text\":\"hello\"}]}]}",
            Encoding.UTF8,
            "application/json");

        var response = await client.PostAsync("/v1/chat/completions", content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.NotNull(body);
        Assert.Equal("invalid_content", body.Error.Code);
    }

    [Fact]
    public async Task PostChatCompletions_InvalidContentPartTextKind_Returns400()
    {
        var client = _factory.CreateClient();
        var content = new StringContent(
            "{\"model\":\"m365-active\",\"messages\":[{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":1}]}]}",
            Encoding.UTF8,
            "application/json");

        var response = await client.PostAsync("/v1/chat/completions", content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.NotNull(body);
        Assert.Equal("invalid_content", body.Error.Code);
    }

    [Fact]
    public async Task PostChatCompletions_WrongPropertyTypeForMessages_Returns400()
    {
        var client = _factory.CreateClient();
        var content = new StringContent(
            "{\"model\":\"m365-active\",\"messages\":42}",
            Encoding.UTF8,
            "application/json");

        var response = await client.PostAsync("/v1/chat/completions", content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.NotNull(body);
        Assert.Equal("invalid_json", body.Error.Code);
    }

    [Fact]
    public async Task PostChatCompletions_UnknownGenerationParameters_AreIgnored()
    {
        var client = _factory.CreateClient();
        var content = new StringContent(
            "{\"model\":\"m365-active\",\"messages\":[{\"role\":\"user\",\"content\":\"Hello\"}],\"temperature\":0,\"max_tokens\":100,\"stream_options\":{\"include_usage\":true}}",
            Encoding.UTF8,
            "application/json");

        var response = await client.PostAsync("/v1/chat/completions", content);
        // Passes validation; reaches browser readiness check (503 because browser AutoStart is false)
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.NotNull(body);
        Assert.Equal("browser_not_ready", body.Error.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(2)]
    public async Task PostChatCompletions_InvalidN_Returns400(int n)
    {
        var client = _factory.CreateClient();
        var content = new StringContent(
            $"{{\"model\":\"m365-active\",\"messages\":[{{\"role\":\"user\",\"content\":\"Hello\"}}],\"n\":{n}}}",
            Encoding.UTF8,
            "application/json");

        var response = await client.PostAsync("/v1/chat/completions", content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.NotNull(body);
        Assert.Equal("unsupported_parameter", body.Error.Code);
    }

    [Fact]
    public async Task PostChatCompletions_MalformedJson_Returns400WithOpenAiError()
    {
        var client = _factory.CreateClient();
        var content = new StringContent(
            "{\"model\":\"m365-active\",\"messages\":",
            Encoding.UTF8,
            "application/json");

        var response = await client.PostAsync("/v1/chat/completions", content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.NotNull(body);
        Assert.Equal("invalid_json", body.Error.Code);
    }

    [Fact]
    public async Task PostChatCompletions_WrongModel_Returns400WithOpenAiError()
    {
        var client = _factory.CreateClient();
        var request = new ChatCompletionRequest(
            "gpt-4o",
            new List<ChatMessageRequest?> { new("user", "Hello") });

        var response = await client.PostAsJsonAsync("/v1/chat/completions", request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.NotNull(body);
        Assert.Equal("invalid_request_error", body.Error.Type);
        Assert.Equal("model_not_found", body.Error.Code);
    }

    [Fact]
    public async Task PostChatCompletions_StreamingBeforeSend_ReturnsJsonError()
    {
        var client = _factory.CreateClient();
        var request = new ChatCompletionRequest(
            "m365-active",
            new List<ChatMessageRequest?> { new("user", "Hello") },
            stream: true);

        var response = await client.PostAsJsonAsync("/v1/chat/completions", request);
        // Failures before Send is clicked keep their status code and JSON body, even for stream: true
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.NotNull(body);
        Assert.Equal("browser_not_ready", body.Error.Code);
    }

    [Fact]
    public async Task PostChatCompletions_SystemAndHistory_AreAccepted()
    {
        var client = _factory.CreateClient();
        var request = new ChatCompletionRequest(
            "m365-active",
            new List<ChatMessageRequest?>
            {
                new("system", "You are an assistant."),
                new("user", "Hello"),
                new("assistant", "Hi"),
                new("user", "Next")
            },
            tools: new[] { new { type = "function", function = new { name = "read_file" } } });

        var response = await client.PostAsJsonAsync("/v1/chat/completions", request);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.NotNull(body);
        Assert.Equal("browser_not_ready", body.Error.Code);
    }

    [Fact]
    public async Task PostChatCompletions_EndsWithAssistantMessage_Returns400()
    {
        var client = _factory.CreateClient();
        var request = new ChatCompletionRequest(
            "m365-active",
            new List<ChatMessageRequest?> { new("user", "Hello"), new("assistant", "Hi") });

        var response = await client.PostAsJsonAsync("/v1/chat/completions", request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.NotNull(body);
        Assert.Equal("no_new_message", body.Error.Code);
    }

    [Fact]
    public async Task PostChatCompletions_EmptyMessageContent_Returns400()
    {
        var client = _factory.CreateClient();
        var request = new ChatCompletionRequest(
            "m365-active",
            new List<ChatMessageRequest?> { new("user", "   ") });

        var response = await client.PostAsJsonAsync("/v1/chat/completions", request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.NotNull(body);
        Assert.Equal("empty_content", body.Error.Code);
    }

    [Fact]
    public async Task GetOpenApiDocument_ListsAllEndpoints()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/openapi/v1.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("/v1/chat/completions", body);
        Assert.Contains("/v1/models", body);
        Assert.Contains("/health/live", body);
        Assert.Contains("/health/ready", body);
    }

    [Fact]
    public async Task GetOpenApiDocument_DescribesClientSetup()
    {
        var client = _factory.CreateClient();
        var body = await client.GetStringAsync("/openapi/v1.json");

        Assert.Contains("Cline", body);
        Assert.Contains("opencode.json", body);
        Assert.Contains("/v1", body);
        Assert.Contains("browser-coding-agent/m365-active", body);
    }

    [Fact]
    public async Task GetSwaggerUi_ReturnsHtml()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/swagger/index.html");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
    }
}
