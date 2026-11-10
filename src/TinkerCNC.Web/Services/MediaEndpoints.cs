using System.Text;
using TinkerCNC.Host;

namespace TinkerCNC.Web.Services;

/// <summary>HTTP endpoints for video frames and stored recordings.</summary>
public static class MediaEndpoints
{
    private const string Boundary = "tinkercncframe";

    public static IEndpointRouteBuilder MapMediaEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/video/{sourceId}/stream", StreamAsync);
        app.MapGet("/video/{sourceId}/snapshot", Snapshot);
        app.MapGet("/recordings/{file}", Recording);
        return app;
    }

    /// <summary>MJPEG (multipart/x-mixed-replace) stream of a frame source, usable directly in &lt;img src&gt;.</summary>
    private static async Task StreamAsync(string sourceId, FrameHub hub, HttpContext ctx)
    {
        ctx.Response.Headers.CacheControl = "no-cache, no-store";
        ctx.Response.ContentType = $"multipart/x-mixed-replace; boundary={Boundary}";
        var body = ctx.Response.Body;
        try
        {
            await foreach (var frame in hub.SubscribeAsync(sourceId, ctx.RequestAborted))
            {
                var header = Encoding.ASCII.GetBytes(
                    $"--{Boundary}\r\nContent-Type: {FrameHub.ContentTypeOf(frame)}\r\nContent-Length: {frame.Length}\r\n\r\n");
                await body.WriteAsync(header, ctx.RequestAborted);
                await body.WriteAsync(frame, ctx.RequestAborted);
                await body.WriteAsync("\r\n"u8.ToArray(), ctx.RequestAborted);
                await body.FlushAsync(ctx.RequestAborted);
            }
        }
        catch (OperationCanceledException)
        {
            // viewer went away
        }
    }

    private static IResult Snapshot(string sourceId, FrameHub hub) =>
        hub.Latest(sourceId) is { } frame
            ? Results.File(frame, FrameHub.ContentTypeOf(frame), $"{sourceId}_{DateTime.Now:yyyyMMdd_HHmmss}.{FrameHub.ExtensionOf(frame)}")
            : Results.NotFound();

    // Videos and images open in the browser (video with range requests, so players can seek); anything else downloads.
    private static readonly IReadOnlyDictionary<string, string> InlineTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [".mp4"] = "video/mp4",
        [".jpg"] = "image/jpeg",
        [".png"] = "image/png",
        [".bmp"] = "image/bmp",
    };

    private static IResult Recording(string file, RecordingStore store)
    {
        if (store.Find(file) is not { } path) return Results.NotFound();
        return InlineTypes.TryGetValue(Path.GetExtension(path), out var type)
            ? Results.File(path, type, enableRangeProcessing: true)
            : Results.File(path, "application/octet-stream", Path.GetFileName(path));
    }
}
