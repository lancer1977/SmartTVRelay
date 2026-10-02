using SmartTVRelay.Viewer;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<ViewerOptions>(builder.Configuration.GetSection("Viewer"));
builder.Services.Configure<TunerOptions>(builder.Configuration.GetSection("Tuner"));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpClient<ITunerLineup, HttpTunerLineup>(c => c.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddSingleton<IFfmpegRunner, ProcessFfmpegRunner>();
builder.Services.AddSingleton<ChannelPipelineManager>();
builder.Services.AddSingleton<IChannelPipelineRegistry>(sp => sp.GetRequiredService<ChannelPipelineManager>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<ChannelPipelineManager>());

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/channels", async (ITunerLineup lineup, CancellationToken ct) =>
{
    try { return Results.Ok(await lineup.GetChannelsAsync(ct)); }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
    {
        return Results.Problem("Tuner lineup is unavailable.", statusCode: StatusCodes.Status502BadGateway);
    }
});

app.MapGet("/hls/{guideNumber}/{file}", async (string guideNumber, string file, ITunerLineup lineup,
    ChannelPipelineManager manager, Microsoft.Extensions.Options.IOptions<ViewerOptions> options,
    CancellationToken ct) =>
{
    if (!ChannelPipelineManager.IsValidGuideNumber(guideNumber)) return Results.NotFound();
    var isPlaylist = file == "index.m3u8";
    if (!isPlaylist && !(file.EndsWith(".ts", StringComparison.Ordinal) && IsSafeFileName(file)))
        return Results.NotFound();

    string path;
    if (isPlaylist)
    {
        if (manager.GetWorkDirectory(guideNumber) is null)
        {
            IReadOnlyList<ChannelInfo> channels;
            try { channels = await lineup.GetChannelsAsync(ct); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                return Results.Problem("Tuner lineup is unavailable.", statusCode: StatusCodes.Status502BadGateway);
            }
            if (!channels.Any(c => c.GuideNumber == guideNumber)) return Results.NotFound();
        }
        try { path = manager.EnsureStarted(guideNumber); }
        catch (CapacityExceededException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var deadline = DateTime.UtcNow.AddSeconds(options.Value.StartupTimeoutSeconds);
        while (!File.Exists(path))
        {
            if (DateTime.UtcNow >= deadline)
                return Results.Problem("Stream did not become ready in time.", statusCode: StatusCodes.Status504GatewayTimeout);
            await Task.Delay(100, ct);
        }
    }
    else
    {
        var resolved = manager.TouchAndResolve(guideNumber, file);
        if (resolved is null || !File.Exists(resolved)) return Results.NotFound();
        path = resolved;
    }

    manager.TouchAndResolve(guideNumber, file);
    byte[] bytes;
    try
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var ms = new MemoryStream();
        await fs.CopyToAsync(ms, ct);
        bytes = ms.ToArray();
    }
    catch (IOException) { return Results.NotFound(); }

    return Results.File(bytes, isPlaylist ? "application/vnd.apple.mpegurl" : "video/mp2t");
});

app.Run();

static bool IsSafeFileName(string name) =>
    name.Length <= 64 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.') && !name.Contains("..");

/// <summary>Entry point marker so WebApplicationFactory can host the app in tests.</summary>
public partial class Program;
