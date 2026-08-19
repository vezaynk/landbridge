using System.Text.Json;
using Landbridge.Classifier;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

var settings = ClassifierSettings.Load(builder.Configuration, builder.Environment.ContentRootPath);
builder.Services.AddSingleton(settings);
builder.Services.AddSingleton<ILlmJudge, LlmJudge>();
builder.Services.AddScoped<ClassifyPipeline>();

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNameCaseInsensitive = true;
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
});

var app = builder.Build();
app.Logger.LogInformation(
    "classifier fast={Fast} review={Review}",
    settings.Fast.Slug.Wire, settings.Review.Slug.Wire);
app.MapDefaultEndpoints();

app.MapPost("/classify", async (ClassifyRequest body, ClassifyPipeline pipeline, ILoggerFactory logs, CancellationToken ct) =>
{
    var log = logs.CreateLogger("classifier");
    if (string.IsNullOrWhiteSpace(body.Tool))
    {
        log.LogInformation("classifier ask via=bad-request");
        return Results.Json(ClassifyResult.Ask("bad-request"));
    }

    var result = await pipeline.ClassifyAsync(body.Tool, body.Input, body.Messages, ct)
        .ConfigureAwait(false);
    log.LogInformation(
        "classifier {Disposition} via={Via} tool={Tool}",
        result.Disposition, result.Via, body.Tool);
    return Results.Json(result);
});

app.Run();

public partial class Program;
