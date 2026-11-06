namespace TinkerCNC.Web.Services;

/// <summary>Lets a second start of the app bring the running window to the front.</summary>
public static class DesktopEndpoints
{
    public const string ActivatePath = "desktop/activate";

    public static IEndpointRouteBuilder MapDesktopEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/" + ActivatePath, (DesktopWindow window) =>
        {
            window.Activate();
            return Results.NoContent();
        }).DisableAntiforgery();
        return app;
    }
}
