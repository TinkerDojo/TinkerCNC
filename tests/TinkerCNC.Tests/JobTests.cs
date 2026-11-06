using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using TinkerCNC.GCode;
using TinkerCNC.Protocol;
using TinkerCNC.Transfer;

namespace TinkerCNC.Tests;

public class GCodeEstimatorTests
{
    [Fact]
    public void Linear_moves_take_distance_over_feed()
    {
        var analysis = GCodeEstimator.Analyze("G21 G90\nG1 X100 F600\nG1 Y100");
        Assert.Equal(10, analysis.Times.Seconds[1], 3);
        Assert.Equal(10, analysis.Times.Seconds[2], 3);
        Assert.Equal(20, analysis.Times.Total, 3);
    }

    [Fact]
    public void Rapids_use_mkr_max_feedrate_and_dwell_uses_milliseconds()
    {
        var analysis = GCodeEstimator.Analyze(";@MKR|MAXFEEDRATE|value=6000\nG0 X100\nG4 P1500");
        Assert.Equal(1, analysis.Times.Seconds[1], 3);
        Assert.Equal(1.5, analysis.Times.Seconds[2], 3);
    }

    [Fact]
    public void Full_circle_arc_length_is_circumference()
    {
        var analysis = GCodeEstimator.Analyze("G1 X10 Y0 F6000\nG2 X10 Y0 I-10 J0 F600");
        Assert.Equal(2 * Math.PI * 10 / 600 * 60, analysis.Times.Seconds[1], 3);
    }

    [Fact]
    public void Tool_changes_split_segments_and_use_metadata_names()
    {
        const string program = """
            ;@MKR|TOOL|number=1|name=Flat 3.175|type=Flat End
            ;@MKR|TOOL|number=2|name=V-bit 30
            M6 T1
            G1 X60 F600
            M6 T2
            G1 X0 F600
            """;
        var analysis = GCodeEstimator.Analyze(program, new EstimatorSettings(ToolChangeSeconds: 0));

        Assert.Equal(2, analysis.Segments.Count);
        Assert.Equal("T1 · Flat 3.175", analysis.Segments[0].Label);
        Assert.Equal(6, analysis.Segments[0].Seconds, 3);
        Assert.Equal("T2 · V-bit 30", analysis.Segments[1].Label);
        Assert.Equal(6, analysis.Segments[1].StartSeconds, 3);
        Assert.Same(analysis.Segments[1], analysis.SegmentAt(6));
    }

    [Fact]
    public void Remaining_time_scales_with_feed_override_and_metadata_calibrates()
    {
        var analysis = GCodeEstimator.Analyze(";@MKR|TIME|seconds=40\nG1 X100 F600\nG1 X200");
        Assert.Equal(2, analysis.CalibrationFactor!.Value, 3);
        Assert.Equal(40, analysis.Times.Total, 3);
        Assert.Equal(20, analysis.Times.Remaining(3), 3);       // line 3 not yet done
        Assert.Equal(10, analysis.Times.Remaining(3, 200), 3);  // 200 % override halves it
    }
}

public class FileTransferTests
{
    [Fact]
    public void Running_file_reply_is_split_on_pipe()
    {
        var file = RunningFile.Parse("/sd/gcodes/part.nc|0123456789abcdef0123456789abcdef");
        Assert.Equal("/sd/gcodes/part.nc", file!.Path);
        Assert.Equal("part.nc", file.FileName);
        Assert.Equal("0123456789abcdef0123456789abcdef", file.Md5);
        Assert.Null(RunningFile.Parse(""));
    }

    [Fact]
    public void Transfer_packets_roundtrip()
    {
        Assert.Equal(new FileView(12, 1024), FileView.Parse(new FileView(12, 1024).ToPacket()));
        var data = FileData.Parse(new FileData(7, new byte[] { 1, 2, 3 }).ToPacket());
        Assert.Equal(7, data.Sequence);
        Assert.Equal(new byte[] { 1, 2, 3 }, data.Data);
        Assert.Equal(new byte[] { 0, 0, 0, 5 }, FileData.RequestFor(5).ToPacket().Data);
    }

    [Fact]
    public async Task Download_follows_the_xmodem_sequence_and_verifies_md5()
    {
        var content = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(0, 300).Select(i => $"G1 X{i}\n")));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var server = Task.Run(() => FakeFileServer.ServeAsync(listener, content, packetSize: 1000));

        await using var machine = MachineConnection.ForAddress($"127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}");
        machine.StatusPollInterval = null;
        await machine.ConnectAsync();

        var progress = new List<DownloadProgress>();
        var result = await new FileDownloader(machine).DownloadAsync("/sd/gcodes/test.nc", new SyncProgress(progress.Add));

        Assert.Equal(content, result.Content);
        Assert.True(result.Md5Matches);
        Assert.Equal(new[] { 1, 2, 3 }, progress.Select(p => p.Packet));
        var requests = await server.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("download /sd/gcodes/test.nc", requests[0]);
        Assert.Contains("B3:2", requests); // re-requested after the simulated out-of-order packet
        Assert.Equal("B4", requests[^1]);
        listener.Stop();
    }

    [Fact]
    public async Task Download_is_cancelled_when_a_cached_copy_has_the_announced_md5()
    {
        var content = Encoding.ASCII.GetBytes("G1 X1\nG1 X2\n");
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var server = Task.Run(() => FakeFileServer.ServeAsync(listener, content, packetSize: 1000));

        await using var machine = MachineConnection.ForAddress($"127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}");
        machine.StatusPollInterval = null;
        await machine.ConnectAsync();

        var result = await new FileDownloader(machine).DownloadAsync("/sd/gcodes/test.nc", cache: new FixedCache(content));

        Assert.True(result.FromCache);
        Assert.Equal(content, result.Content);
        var requests = await server.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { "download /sd/gcodes/test.nc", "B5" }, requests); // no view or data requested
        listener.Stop();
    }

    private sealed class FixedCache(byte[] content) : IFileCache
    {
        public byte[]? TryGet(string md5) =>
            string.Equals(md5, Convert.ToHexString(MD5.HashData(content)), StringComparison.OrdinalIgnoreCase) ? content : null;
    }

    private sealed class SyncProgress(Action<DownloadProgress> report) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) => report(value);
    }

    /// <summary>Minimal machine side: answers one download, sending packet 3 once out of order.</summary>
    private sealed class FakeFileServer(Stream stream, byte[] content, int packetSize) : IHostMessageVisitor
    {
        private bool _sentOutOfOrder;
        public List<string> Requests { get; } = new();
        public bool Done { get; private set; }

        public static async Task<List<string>> ServeAsync(TcpListener listener, byte[] content, int packetSize)
        {
            using var client = await listener.AcceptTcpClientAsync();
            var server = new FakeFileServer(client.GetStream(), content, packetSize);
            var reader = new PacketReader();
            var buffer = new byte[4096];
            while (!server.Done)
            {
                var n = await client.GetStream().ReadAsync(buffer);
                if (n == 0) break;
                foreach (var packet in reader.Feed(buffer.AsSpan(0, n)))
                    await IHostMessage.From(packet).AcceptAsync(server);
            }
            return server.Requests;
        }

        private int Total => (content.Length + packetSize - 1) / packetSize;

        private Task SendAsync(IHostMessage message) => stream.WriteAsync(message.ToPacket().Encode()).AsTask();

        private FileData Packet(int seq) => new(seq, content.Skip((seq - 1) * packetSize).Take(packetSize).ToArray());

        public Task VisitAsync(FileTransferCommand message)
        {
            Requests.Add(message.Text);
            return SendAsync(new FileMd5(Convert.ToHexString(MD5.HashData(content)).ToLowerInvariant()));
        }

        public Task VisitAsync(FileView message)
        {
            Requests.Add("B2");
            return SendAsync(new FileView(Total, packetSize));
        }

        public Task VisitAsync(FileData message)
        {
            Requests.Add($"B3:{message.Sequence}");
            if (message.Sequence == 2 && !_sentOutOfOrder)
            {
                _sentOutOfOrder = true;
                return SendAsync(Packet(3));
            }
            return SendAsync(Packet(message.Sequence));
        }

        public Task VisitAsync(FileEnd message)
        {
            Requests.Add("B4");
            Done = true;
            return Task.CompletedTask;
        }

        public Task VisitAsync(RealtimeCommand message) => Task.CompletedTask;
        public Task VisitAsync(TextCommand message) => Task.CompletedTask;
        public Task VisitAsync(FileQueryRequest message) => Task.CompletedTask;
        public Task VisitAsync(FileMd5 message) => Task.CompletedTask;
        public Task VisitAsync(FileCancel message)
        {
            Requests.Add("B5");
            Done = true;
            return Task.CompletedTask;
        }
        public Task VisitAsync(FileRetry message) => Task.CompletedTask;
        public Task VisitAsync(UnknownHostMessage message) => Task.CompletedTask;
    }
}
