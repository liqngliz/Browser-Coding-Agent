using BrowserCodingAgent.Api.Browser;
using BrowserCodingAgent.Api.Configuration;
using BrowserCodingAgent.Api.Contracts;
using BrowserCodingAgent.Api.OpenApi;
using BrowserCodingAgent.Api.Providers.M365;
using BrowserCodingAgent.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<BrowserOptions>()
    .Bind(builder.Configuration.GetSection(BrowserOptions.SectionName))
    .Validate(o => { o.Validate(); return true; })
    .ValidateOnStart();

builder.Services.AddOptions<ChatOptions>()
    .Bind(builder.Configuration.GetSection(ChatOptions.SectionName))
    .Validate(o => { o.Validate(); return true; })
    .ValidateOnStart();

builder.Services.AddSingleton<BrowserSession>();
builder.Services.AddSingleton<IBrowserSession>(sp => sp.GetRequiredService<BrowserSession>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<BrowserSession>());
builder.Services.AddSingleton<M365ChatAdapter>();
builder.Services.AddSingleton(sp => PromptReplacements.Load(
    sp.GetRequiredService<IOptions<ChatOptions>>().Value.ReplacementsFile,
    AppContext.BaseDirectory,
    sp.GetRequiredService<ILogger<PromptReplacements>>()));
builder.Services.AddSingleton<RequestCoordinator>();

builder.Services.AddOpenApi(options => options.AddClientSetupDescription());

var app = builder.Build();

// Load replacement rules at startup so a broken file fails fast instead of on the first request
app.Services.GetRequiredService<PromptReplacements>();

app.Use(async (context, next) =>
{
    try
    {
        await next(context);
    }
    catch (BadHttpRequestException ex)
    {
        if (!context.Response.HasStarted)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            context.Response.ContentType = "application/json";
            var err = new ErrorResponse(new ErrorDetail(ex.Message, "invalid_request_error", null, "invalid_json"));
            await context.Response.WriteAsJsonAsync(err);
        }
        return;
    }

    if (context.Response.StatusCode == StatusCodes.Status400BadRequest &&
        !context.Response.HasStarted &&
        (context.Response.ContentType == null || !context.Response.ContentType.Contains("json")))
    {
        context.Response.ContentType = "application/json";
        var err = new ErrorResponse(new ErrorDetail(
            "Malformed JSON or invalid request payload.",
            "invalid_request_error",
            null,
            "invalid_json"));
        await context.Response.WriteAsJsonAsync(err);
    }
});

// Loopback tool: docs are served in every environment, including packaged (Production) runs
app.MapOpenApi();
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/openapi/v1.json", "Browser Coding Agent v1");
    options.RoutePrefix = "swagger";
});

app.MapGet("/health/live", () => Results.Ok(new HealthLiveResponse("live")))
    .WithTags("Health")
    .WithSummary("Process is running")
    .Produces<HealthLiveResponse>();

app.MapGet("/health/ready", async (
    IBrowserSession session,
    M365ChatAdapter adapter,
    RequestCoordinator coordinator) =>
{
    var (coordReady, coordReason) = await coordinator.CheckCoordinatorReadyAsync();
    if (!coordReady)
    {
        return Results.Json(
            new HealthReadyResponse("not_ready", coordReason),
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    var (ready, reason) = await session.CheckReadyAsync(adapter);
    if (ready)
    {
        return Results.Ok(new HealthReadyResponse("ready"));
    }

    return Results.Json(
        new HealthReadyResponse("not_ready", reason),
        statusCode: StatusCodes.Status503ServiceUnavailable);
})
    .WithTags("Health")
    .WithSummary("Browser, tab, and composer are idle and ready")
    .Produces<HealthReadyResponse>()
    .Produces<HealthReadyResponse>(StatusCodes.Status503ServiceUnavailable);

app.MapGet("/v1/models", (IOptions<ChatOptions> chatOptions) =>
{
    var alias = chatOptions.Value.ModelAlias;
    var response = new ModelsResponse(
        Object: "list",
        Data: new List<ModelData>
        {
            new(
                Id: alias,
                Object: "model",
                Created: 1700000000,
                OwnedBy: "browser-coding-agent")
        });

    return Results.Ok(response);
})
    .WithTags("OpenAI")
    .WithSummary("Configured model alias")
    .Produces<ModelsResponse>();

app.MapPost("/v1/chat/completions", async (
    ChatCompletionRequest request,
    RequestCoordinator coordinator,
    HttpContext httpContext) =>
{
    return await coordinator.ExecuteChatCompletionAsync(request, httpContext);
})
    .WithTags("OpenAI")
    .WithSummary("Send one user message to M365 Copilot and return the completed answer")
    .Produces<ChatCompletionResponse>()
    .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
    .Produces<ErrorResponse>(StatusCodes.Status409Conflict)
    .Produces<ErrorResponse>(StatusCodes.Status500InternalServerError)
    .Produces<ErrorResponse>(StatusCodes.Status502BadGateway)
    .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable)
    .Produces<ErrorResponse>(StatusCodes.Status504GatewayTimeout);

app.Run();

public partial class Program { }
