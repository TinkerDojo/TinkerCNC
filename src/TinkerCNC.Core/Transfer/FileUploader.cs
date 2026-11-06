using System.Security.Cryptography;
using TinkerCNC.Protocol;

namespace TinkerCNC.Transfer;

public sealed record UploadProgress(int Packet, int TotalPackets, long Bytes)
{
    public double Fraction => TotalPackets > 0 ? (double)Packet / TotalPackets : 0;
}

/// <summary>
/// Uploads a file to the machine's SD card with the same packets as a download, roles reversed: the host sends
/// 0xB0 "upload &lt;path&gt;" and the MD5 (B1), and the machine then asks for everything else: an empty B2 for the view (packet count u32, packet size u16), B3 with a sequence number for that packet;
/// B4 after the last packet means the file is stored, B6 asks to resend the last reply and B5 cancels. The machine
/// refuses uploads while it is moving (B5 right away) and gives up after about 30 s without progress. While it waits
/// for packets it ignores everything else. It empties its receive buffer once it has opened the file, so an MD5 sent
/// right after the command can be lost: it asks again with B6 after about 5 s (an empty B1 only after many wrong
/// packets), and the host sends it again on its own after <see cref="Md5Resend"/>. The machine keeps the MD5 next to
/// the file and reports it with the running file (0xB7). (firmware, Player upload.)
/// Status polling is suspended for the duration.
/// </summary>
public sealed class FileUploader(MachineConnection machine)
{
    /// <summary>Largest data block per packet the machine accepts; used over the network.</summary>
    public const int NetworkPacketSize = 8192;

    public int PacketSize { get; init; } = NetworkPacketSize;

    /// <summary>Wait for the first request before sending the MD5 again.</summary>
    public TimeSpan Md5Resend { get; init; } = TimeSpan.FromSeconds(1.5);

    /// <summary>Wait for the machine's next request before resending the last reply.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Consecutive timeouts before giving up (the machine gives up after about 30 s itself).</summary>
    public int MaxRetries { get; init; } = 2;

    public async Task UploadAsync(
        string remotePath, byte[] content, IProgress<UploadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (content.Length == 0) throw new ArgumentException("An empty file can't be uploaded.", nameof(content));
        using var inbox = new MessageInbox(machine);
        using var polling = machine.SuspendStatusPolling();
        var session = new UploadSession(content, PacketSize, progress);
        try
        {
            await machine.SendAsync(FileTransferCommand.Upload(remotePath), cancellationToken).ConfigureAwait(false);
            await machine.SendAsync(session.Md5, cancellationToken).ConfigureAwait(false);
            if (await inbox.ReadAsync(Md5Resend, cancellationToken).ConfigureAwait(false) is { } first)
                await AnswerAsync(session, first, cancellationToken).ConfigureAwait(false);
            else
                await machine.SendAsync(session.Md5, cancellationToken).ConfigureAwait(false); // probably lost
            await RunAsync(session, inbox, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!session.CancelledByMachine)
        {
            await CancelAsync().ConfigureAwait(false); // or the machine keeps waiting for packets for half a minute
            throw;
        }
    }

    /// <summary>Tells the machine to stop waiting; a connection that is gone needs nothing.</summary>
    private async Task CancelAsync()
    {
        try
        {
            await machine.SendAsync(new FileCancel(), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or ObjectDisposedException)
        {
        }
    }

    private async Task AnswerAsync(UploadSession session, IMachineMessage message, CancellationToken ct)
    {
        message.Accept(session);
        foreach (var reply in session.TakeOutgoing())
            await machine.SendAsync(reply, ct).ConfigureAwait(false);
    }

    private async Task RunAsync(UploadSession session, MessageInbox inbox, CancellationToken ct)
    {
        var timeouts = 0;
        while (!session.IsComplete)
        {
            var message = await inbox.ReadAsync(RequestTimeout, ct).ConfigureAwait(false);
            if (message is null)
            {
                if (++timeouts > MaxRetries)
                    throw new FileTransferException($"The machine stopped asking for data ({session.Describe()}).");
                await machine.SendAsync(session.LastReply, ct).ConfigureAwait(false);
                continue;
            }

            timeouts = 0;
            await AnswerAsync(session, message, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Answers the machine's requests from the file content.</summary>
    private sealed class UploadSession : MachineMessageVisitor
    {
        private readonly byte[] _content;
        private readonly int _packetSize;
        private readonly IProgress<UploadProgress>? _progress;
        private readonly List<IHostMessage> _outgoing = new();
        private int _lastPacket;

        public UploadSession(byte[] content, int packetSize, IProgress<UploadProgress>? progress)
        {
            _content = content;
            _packetSize = packetSize;
            _progress = progress;
            TotalPackets = (content.Length + packetSize - 1) / packetSize;
            Md5 = new FileMd5(Convert.ToHexString(MD5.HashData(content)).ToLowerInvariant());
            LastReply = Md5;
        }

        public FileMd5 Md5 { get; }
        public int TotalPackets { get; }
        public IHostMessage LastReply { get; private set; }
        public bool IsComplete { get; private set; }

        /// <summary>The machine ended the upload itself (B5): nothing to cancel.</summary>
        public bool CancelledByMachine { get; private set; }

        public string Describe() => $"packet {_lastPacket} of {TotalPackets} sent";

        public IReadOnlyList<IHostMessage> TakeOutgoing()
        {
            var messages = _outgoing.ToArray();
            _outgoing.Clear();
            return messages;
        }

        public override void Visit(FileMd5 message) => Reply(Md5);
        public override void Visit(FileView message) => Reply(new FileView(TotalPackets, _packetSize));

        public override void Visit(FileData message)
        {
            if (message.Sequence < 1 || message.Sequence > TotalPackets)
                throw new FileTransferException($"The machine asked for packet {message.Sequence} of {TotalPackets}.");
            var offset = (message.Sequence - 1) * _packetSize;
            var length = Math.Min(_packetSize, _content.Length - offset);
            Reply(new FileData(message.Sequence, _content.AsSpan(offset, length).ToArray()));
            _lastPacket = message.Sequence;
            _progress?.Report(new UploadProgress(message.Sequence, TotalPackets, offset + length));
        }

        public override void Visit(FileEnd message) => IsComplete = true;
        public override void Visit(FileRetry message) => _outgoing.Add(LastReply);

        public override void Visit(FileCancel message)
        {
            CancelledByMachine = true;
            throw new FileTransferException("The machine cancelled the upload. It accepts files only while it is not moving.");
        }

        public override void Visit(AlarmMessage message) => throw new FileTransferException($"Alarm during upload: {message.Text.Trim()}");

        private void Reply(IHostMessage reply)
        {
            LastReply = reply;
            _outgoing.Add(reply);
        }
    }
}
