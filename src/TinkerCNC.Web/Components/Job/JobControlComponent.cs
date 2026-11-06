using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using TinkerCNC.Host;
using TinkerCNC.Web.Components.Machine;

namespace TinkerCNC.Web.Components.Job;

/// <summary>A panel that starts or controls programs on the session's machine (<see cref="JobControl"/>).</summary>
public abstract class JobControlComponent : SessionComponent
{
    [Inject] private JobControls Controls { get; set; } = default!;

    protected JobControl Control => Controls.Get(Session);

    protected bool Playing => Session.Status?.IsPlaying == true;

    /// <summary>File types offered when choosing a G-code file.</summary>
    protected const string ProgramFileTypes = ".nc,.gcode,.ngc,.tap,.txt,.cnc";

    private const long MaxProgramSize = 100 * 1024 * 1024;

    /// <summary>Reads a G-code file chosen in the browser.</summary>
    protected static async Task<byte[]> ReadAsync(IBrowserFile file)
    {
        using var buffer = new MemoryStream();
        await file.OpenReadStream(maxAllowedSize: MaxProgramSize).CopyToAsync(buffer);
        return buffer.ToArray();
    }
}
