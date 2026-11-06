namespace TinkerCNC.Storage;

/// <summary>
/// Per-user application data folders: <c>%APPDATA%</c> (Roaming) on Windows and <c>~/Library/Application Support</c>
/// on macOS (.NET maps LocalApplicationData there since .NET 8; ApplicationData would be <c>~/.config</c>).
/// </summary>
public static class UserDataFolders
{
    public static string Root => Environment.GetFolderPath(OperatingSystem.IsMacOS()
        ? Environment.SpecialFolder.LocalApplicationData
        : Environment.SpecialFolder.ApplicationData);

    /// <summary>This app's data: history database, G-code cache, saved programs, machines, recordings.</summary>
    public static string TinkerCNC => Path.Combine(Root, "TinkerCNC");
}
