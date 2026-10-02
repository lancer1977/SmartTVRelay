using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using SmartTVRelay.Viewer.State;

namespace SmartTVRelay.Viewer;

public static class ChannelStateEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Registers fused-state services (evidence source, service, monitor, options).</summary>
    public static IServiceCollection AddChannelState(this IServiceCollection services)
    {
        services.AddOptions<ChannelStateOptions>().BindConfiguration("Viewer:State");
        services.TryAddSingleton<IChannelEvidenceSource, SegmentEvidenceSource>();
        services.AddSingleton<ChannelStateService>();
        services.AddHostedService<ChannelStateMonitor>();
        return services;
    }

    /// <summary>Maps <c>/api/channels/{guideNumber}/state</c> and <c>/events</c>.</summary>
    public static IEndpointRouteBuilder MapChannelState(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/channels/{guideNumber}/state", async (string guideNumber, ChannelStateService svc, CancellationToken ct) =>
        {
            var state = await svc.GetStateAsync(guideNumber, ct);
            return state is null ? Results.NotFound() : Results.Json(state, Json);
        });

        app.MapGet("/api/channels/{guideNumber}/events", async (HttpContext ctx, string guideNumber,
            ChannelStateService svc, IOptions<ChannelStateOptions> options) =>
        {
            var ct = ctx.RequestAborted;
            using var sub = await svc.SubscribeAsync(guideNumber, ct);
            if (sub is null) { ctx.Response.StatusCode = StatusCodes.Status404NotFound; return; }

            ctx.Response.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";

            async Task Write(string text)
            {
                await ctx.Response.WriteAsync(text, ct);
                await ctx.Response.Body.FlushAsync(ct);
            }

            var keepAlive = TimeSpan.FromSeconds(Math.Max(1, options.Value.KeepAliveSeconds));
            try
            {
                var last = sub.Initial.State;
                await Write($"data: {JsonSerializer.Serialize(sub.Initial, Json)}\n\n");
                while (!ct.IsCancellationRequested)
                {
                    using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    wait.CancelAfter(keepAlive);
                    try
                    {
                        if (!await sub.Reader.WaitToReadAsync(wait.Token)) break; // channel stopped
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        await Write(": keep-alive\n\n");
                        continue;
                    }
                    while (sub.Reader.TryRead(out var dto))
                    {
                        if (dto.State == last) continue;
                        last = dto.State;
                        await Write($"data: {JsonSerializer.Serialize(dto, Json)}\n\n");
                    }
                }
            }
            catch (OperationCanceledException) { /* client went away */ }
        });

        return app;
    }
}
