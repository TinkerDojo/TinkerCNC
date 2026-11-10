using System.Globalization;
using System.Text;
using TinkerCNC.Protocol;
using TinkerCNC.Localization;

namespace TinkerCNC.Transfer;

/// <summary>A file or directory on the machine's SD card.</summary>
/// <param name="Modified">Local time of the last change, when the machine reported a valid one.</param>
public sealed record RemoteEntry(string Name, bool IsDirectory, long Size, DateTime? Modified);

/// <summary>
/// Directory listings: the text command <c>ls -e -s &lt;dir&gt;</c> answers with one or more 0x83 packets of lines
/// <c>name size yyyyMMddHHmmss</c> (directories end in <c>/</c> and have size 0, special characters in names encoded as
/// in <see cref="RemotePath"/>, hidden names starting with a dot are left out), then 0x84; a directory that can't be
/// opened answers 0x85. <c>-s</c> adds size and date, <c>-e</c> an end marker (firmware, SimpleShell ls).
/// </summary>
public static class RemoteListing
{
    private const string DateFormat = "yyyyMMddHHmmss";

    public static string Command(string directory) => $"ls -e -s {RemotePath.Encode(directory)}";

    public static IReadOnlyList<RemoteEntry> Parse(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ParseLine)
            .OfType<RemoteEntry>()
            .ToList();

    private static RemoteEntry? ParseLine(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3 || !long.TryParse(parts[^2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var size))
            return null;
        var name = RemotePath.Decode(string.Join(' ', parts[..^2]));
        var modified = DateTime.TryParseExact(parts[^1], DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
            ? t
            : (DateTime?)null;
        var isDirectory = name.EndsWith('/');
        return new RemoteEntry(isDirectory ? name.TrimEnd('/') : name, isDirectory, size, modified);
    }
}

/// <summary>Lists a directory on the machine's SD card (<see cref="RemoteListing"/>).</summary>
public sealed class RemoteDirectoryReader(MachineConnection machine)
{
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <exception cref="FileTransferException">The machine could not open the directory.</exception>
    /// <exception cref="TimeoutException">The listing did not finish in time.</exception>
    public async Task<IReadOnlyList<RemoteEntry>> ListAsync(string directory, CancellationToken cancellationToken = default)
    {
        using var inbox = new MessageInbox(machine);
        var listing = new Listing();
        await machine.SendCommandAsync(RemoteListing.Command(directory), cancellationToken).ConfigureAwait(false);
        var deadline = DateTime.UtcNow + Timeout;
        while (!listing.IsComplete)
        {
            var left = deadline - DateTime.UtcNow;
            var message = left > TimeSpan.Zero ? await inbox.ReadAsync(left, cancellationToken).ConfigureAwait(false) : null;
            if (message is null) throw new LocalizedTimeoutException(CoreTexts.Of("Transfer.ListTimeout", directory, Timeout.TotalSeconds));
            message.Accept(listing);
        }
        return RemoteListing.Parse(listing.Text);
    }

    private sealed class Listing : MachineMessageVisitor
    {
        private readonly StringBuilder _text = new();

        public bool IsComplete { get; private set; }
        public string Text => _text.ToString();

        public override void Visit(LoadInfoMessage message) => _text.Append(message.Text).Append('\n');
        public override void Visit(LoadFinishedMessage message) => IsComplete = true;
        public override void Visit(LoadErrorMessage message) => throw new FileTransferException(Localization.Text.Plain(message.Text.Trim()));
    }
}
