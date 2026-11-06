using System.Runtime.InteropServices;
using TinkerCNC.GCode;
using TinkerCNC.Host;

namespace TinkerCNC.Web.Services;

/// <summary>
/// Serves the toolpath of a machine's current program to the 3D view (<c>wwwroot/js/toolpath-view.js</c>) as one
/// little-endian binary block, which is far smaller and faster than JSON for hundreds of thousands of points:
/// <c>"TPTH"</c>, uint32 version, uint32 count, float32[count × 3] positions, int32[count] lines, uint8[count] moves.
/// </summary>
public static class ToolpathEndpoints
{
    private const uint Version = 1;

    public static IEndpointRouteBuilder MapToolpathEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/toolpath/{address}", WriteAsync);
        return app;
    }

    private static async Task<IResult> WriteAsync(string address, MachineSessionManager sessions, JobTrackers trackers, HttpContext ctx)
    {
        if (sessions.Find(address) is not { } session || trackers.Get(session).Timeline is not { } timeline)
            return Results.NotFound();

        var path = timeline.Analysis.Toolpath;
        ctx.Response.ContentType = "application/octet-stream";
        ctx.Response.Headers.CacheControl = "no-store";
        var body = ctx.Response.Body;
        await body.WriteAsync("TPTH"u8.ToArray(), ctx.RequestAborted);
        await body.WriteAsync(MemoryMarshal.AsBytes<uint>([Version, (uint)path.Count]).ToArray(), ctx.RequestAborted);
        await body.WriteAsync(MemoryMarshal.AsBytes(path.Positions.AsSpan()).ToArray(), ctx.RequestAborted);
        await body.WriteAsync(MemoryMarshal.AsBytes(path.Lines.AsSpan()).ToArray(), ctx.RequestAborted);
        await body.WriteAsync(MemoryMarshal.AsBytes(path.Moves.AsSpan()).ToArray(), ctx.RequestAborted);
        return Results.Empty;
    }
}
