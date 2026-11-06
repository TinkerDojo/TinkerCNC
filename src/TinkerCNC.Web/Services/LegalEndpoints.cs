namespace TinkerCNC.Web.Services;

/// <summary>Serves the license texts that ship with the app (<c>legal/</c> next to the executable) as plain text.</summary>
public static class LegalEndpoints
{
    public static IEndpointRouteBuilder MapLegalEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/legal/{file}", Serve);
        return app;
    }

    private static IResult Serve(string file)
    {
        if (!AppInfo.LegalFiles.Any(f => f.File == file)) return Results.NotFound();
        var path = Path.Combine(AppContext.BaseDirectory, "legal", file);
        return File.Exists(path) ? Results.File(path, "text/plain; charset=utf-8") : Results.NotFound();
    }
}
