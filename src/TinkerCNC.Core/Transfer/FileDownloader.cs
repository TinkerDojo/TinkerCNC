using System.Security.Cryptography;
using TinkerCNC.Protocol;

namespace TinkerCNC.Transfer;

public sealed record DownloadProgress(int Packet, int TotalPackets, long Bytes)
{
    public double Fraction => TotalPackets > 0 ? (double)Packet / TotalPackets : 0;
}

/// <param name="Md5">MD5 announced by the machine (B1), if any.</param>
/// <param name="Md5Matches">Whether the content matches <paramref name="Md5"/>; null when no MD5 was announced.</param>
/// <param name="FromCache">True when a local copy with the announced MD5 was used and no data was transferred.</param>
public sealed record DownloadResult(string RemotePath, byte[] Content, string? Md5, bool? Md5Matches, bool FromCache = false);

/// <summary>Local copies of files, looked up by the MD5 the machine announces.</summary>
public interface IFileCache
{
    byte[]? TryGet(string md5);
}

public sealed class FileTransferException(string message) : Exception(message);

/// <summary>
/// Downloads a file from the machine's SD card with the XMODEM-like file transfer; see
/// <see cref="FileTransferPayload"/> for the packet sequence. Status polling is suspended for the duration.
/// <para>
/// With an <see cref="IFileCache"/>, the MD5 the machine announces first (B1) is looked up locally; on a hit the
/// transfer is cancelled with B5 and the local copy is returned.
/// </para>
/// </summary>
public sealed class FileDownloader(MachineConnection machine)
{
    public TimeSpan PacketTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Consecutive timeouts before giving up.</summary>
    public int MaxRetries { get; init; } = 5;

    public async Task<DownloadResult> DownloadAsync(
        string remotePath, IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default,
        IFileCache? cache = null)
    {
        using var inbox = new MessageInbox(machine);
        using var polling = machine.SuspendStatusPolling();
        var session = new DownloadSession(progress, cache);
        await machine.SendAsync(FileTransferCommand.Download(remotePath), cancellationToken).ConfigureAwait(false);
        await RunAsync(session, inbox, cancellationToken).ConfigureAwait(false);
        return session.ToResult(remotePath);
    }

    private async Task RunAsync(DownloadSession session, MessageInbox inbox, CancellationToken ct)
    {
        var timeouts = 0;
        while (!session.IsComplete)
        {
            var message = await inbox.ReadAsync(PacketTimeout, ct).ConfigureAwait(false);
            if (message is null)
            {
                if (++timeouts > MaxRetries)
                    throw new FileTransferException($"No response from the machine after {MaxRetries} retries.");
                await machine.SendAsync(session.LastRequest, ct).ConfigureAwait(false);
                continue;
            }

            timeouts = 0;
            message.Accept(session);
            foreach (var request in session.TakeOutgoing())
                await machine.SendAsync(request, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Protocol state shared by the phase objects.</summary>
    private sealed class DownloadSession : MachineMessageVisitor
    {
        private readonly IProgress<DownloadProgress>? _progress;
        private readonly List<IHostMessage> _outgoing = new();
        private readonly MemoryStream _content = new();
        private TransferPhase _phase = new AwaitingMd5();
        private bool _fromCache;

        public DownloadSession(IProgress<DownloadProgress>? progress, IFileCache? cache)
        {
            _progress = progress;
            Cache = cache;
        }

        public IFileCache? Cache { get; }

        /// <summary>Resent after a timeout or a RETRY; before the MD5 arrives this is an empty B1.</summary>
        public IHostMessage LastRequest { get; private set; } = FileMd5.Request;

        public string? Md5 { get; set; }
        public int TotalPackets { get; set; }
        public bool IsComplete { get; private set; }

        public void Request(IHostMessage request)
        {
            LastRequest = request;
            _outgoing.Add(request);
        }

        public void MoveTo(TransferPhase phase) => _phase = phase;

        public void Append(FileData packet)
        {
            _content.Write(packet.Data);
            _progress?.Report(new DownloadProgress(packet.Sequence, TotalPackets, _content.Length));
        }

        public void Complete()
        {
            _outgoing.Add(new FileEnd());
            IsComplete = true;
        }

        /// <summary>The local copy is identical: cancel the transfer (B5) and use it.</summary>
        public void CompleteFromCache(byte[] content)
        {
            _content.Write(content);
            _fromCache = true;
            _outgoing.Add(new FileCancel());
            IsComplete = true;
        }

        public IReadOnlyList<IHostMessage> TakeOutgoing()
        {
            var messages = _outgoing.ToArray();
            _outgoing.Clear();
            return messages;
        }

        public DownloadResult ToResult(string remotePath)
        {
            var content = _content.ToArray();
            bool? matches = Md5 is { Length: 32 } md5
                ? string.Equals(Convert.ToHexString(MD5.HashData(content)), md5, StringComparison.OrdinalIgnoreCase)
                : null;
            return new DownloadResult(remotePath, content, Md5, matches, _fromCache);
        }

        public override void Visit(FileMd5 message) => _phase.OnMd5(this, message);
        public override void Visit(FileView message) => _phase.OnView(this, message);
        public override void Visit(FileData message) => _phase.OnData(this, message);
        public override void Visit(FileRetry message) => _outgoing.Add(LastRequest);
        public override void Visit(FileCancel message) => throw new FileTransferException("The machine cancelled the transfer.");
        public override void Visit(AlarmMessage message) => throw new FileTransferException($"Alarm during transfer: {message.Text.Trim()}");
    }

    /// <summary>One phase of the download; ignores messages that do not belong to it.</summary>
    private abstract class TransferPhase
    {
        public virtual void OnMd5(DownloadSession session, FileMd5 message) { }
        public virtual void OnView(DownloadSession session, FileView message) { }
        public virtual void OnData(DownloadSession session, FileData message) { }
    }

    /// <summary>WAIT_MD5: the machine announces the MD5, then the host asks for the view.</summary>
    private sealed class AwaitingMd5 : TransferPhase
    {
        public override void OnMd5(DownloadSession session, FileMd5 message)
        {
            session.Md5 = message.Md5;
            if (message.Md5.Length > 0 && session.Cache?.TryGet(message.Md5) is { } cached)
            {
                session.CompleteFromCache(cached);
                return;
            }
            session.Request(FileView.Request);
            session.MoveTo(new AwaitingView());
        }
    }

    /// <summary>WAIT_FILE_VIEW: packet count and size; an invalid count is requested again.</summary>
    private sealed class AwaitingView : TransferPhase
    {
        public override void OnView(DownloadSession session, FileView message)
        {
            if (message.TotalPackets <= 0)
            {
                session.Request(FileView.Request);
                return;
            }
            session.TotalPackets = message.TotalPackets;
            session.Request(FileData.RequestFor(1));
            session.MoveTo(new ReceivingData(1));
        }
    }

    /// <summary>READ_FILE_DATA: packets are requested one by one; out-of-order packets are requested again.</summary>
    private sealed class ReceivingData(int expected) : TransferPhase
    {
        private int _expected = expected;

        public override void OnData(DownloadSession session, FileData message)
        {
            if (message.Sequence != _expected)
            {
                session.Request(FileData.RequestFor(_expected));
                return;
            }

            session.Append(message);
            if (_expected == session.TotalPackets)
            {
                session.Complete();
                return;
            }
            session.Request(FileData.RequestFor(++_expected));
        }
    }
}
