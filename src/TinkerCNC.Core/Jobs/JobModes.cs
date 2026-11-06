namespace TinkerCNC.Jobs;

/// <summary>
/// A job option the machine keeps once switched: on or off with a single command, it then applies while programs run
/// (for example the vacuum running whenever the spindle does). The host sends the chosen state of every option of
/// the machine right before starting a program.
/// </summary>
/// <param name="Key">Stable name for saved settings.</param>
public sealed record JobMode(string Key, string Name, string Description, string OnCommand, string OffCommand)
{
    public string CommandFor(bool on) => on ? OnCommand : OffCommand;
}

/// <summary>
/// The known job options. <c>M331</c>/<c>M332</c> (vacuum) and <c>M331.3</c>/<c>M332.3</c> (external output) switch
/// modes in which that output follows the spindle (firmware, SimpleShell M331/M332). Compact machines also take
/// <c>M331.1</c>, <c>M331.2</c> and <c>M331.4</c> (off: <c>M332.x</c>) and <c>M951</c>/<c>M952</c>; what exactly
/// they do is up to its firmware: sent on their own they change nothing visible, as they only take effect while a
/// program runs.
/// </summary>
public static class JobModes
{
    public static readonly JobMode Vacuum = new("vacuum", "Auto vacuum",
        "The vacuum runs whenever the spindle does.", "M331", "M332");

    public static readonly JobMode ExternalOutput = new("extout", "Auto external output",
        "The external output follows the spindle.", "M331.3", "M332.3");

    public static readonly JobMode Blow = new("blow", "Auto blow",
        "The spindle blower clears chips while cutting.", "M331.1", "M332.1");

    public static readonly JobMode BedClean = new("bedclean", "Auto bed clean",
        "The machine cleans the bed after the program.", "M331.2", "M332.2");

    public static readonly JobMode AntiStatic = new("antistatic", "Anti-static",
        "Static charge is removed while cutting.", "M331.4", "M332.4");

    public static readonly JobMode TimeLapse = new("timelapse", "Auto time-lapse",
        "The camera records a time-lapse of the program.", "M951", "M952");
}
