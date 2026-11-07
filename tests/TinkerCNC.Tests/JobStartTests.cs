using System.Net;
using Microsoft.Extensions.Configuration;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using TinkerCNC.GCode;
using TinkerCNC.Jobs;
using TinkerCNC.Motion;
using TinkerCNC.Protocol;
using TinkerCNC.Transfer;

namespace TinkerCNC.Tests;

public class JobStartPlanTests
{
    private static readonly MachineTravel CompactTravel = new(
        new AxisRange(-207, 0), new AxisRange(-206, 0), new AxisRange(-102, 0), -190.3, -194.114, -3);

    private static readonly ToolpathBounds Program = new(10, 20, -3, 110, 80, 5);

    private static JobStartContext Context(int? tool = 1, ToolpathBounds? program = null, MachineTravel? travel = null) =>
        new(travel ?? CompactTravel, program ?? Program, [-100, -120, -10], tool, MachineTypes.JobModesFor(travel ?? CompactTravel));

    [Fact]
    public void Without_probing_the_machine_goes_to_the_program_corner_first()
    {
        Assert.Equal("M495 X10 Y20 P1", JobStartPlan.Automation(new JobStartOptions(), Program));
    }

    [Fact]
    public void Probing_options_add_their_words_in_firmware_order()
    {
        var options = new JobStartOptions
        {
            ScanMargin = true, ZProbe = true, ZProbeOffsetX = 5, ZProbeOffsetY = 6,
            Leveling = true, LevelingPointsX = 5, LevelingPointsY = 4, LevelingHeight = 3,
        };
        Assert.Equal("M495 X10 Y20 C110 D80 O5 F6 A100 B60 I5 J4 H3", JobStartPlan.Automation(options, Program));
    }

    [Fact]
    public void A_z_probe_point_from_the_work_origin_is_sent_relative_to_the_program_corner()
    {
        var options = new JobStartOptions { ZProbe = true, ZProbeFromWorkOrigin = true, ZProbeOffsetX = 15, ZProbeOffsetY = 25 };
        Assert.Equal("M495 X10 Y20 O5 F5", JobStartPlan.Automation(options, Program));
    }

    [Fact]
    public void Z_probe_at_the_center_counts_half_the_area_from_the_program_corner()
    {
        var options = new JobStartOptions { ZProbe = true, ZProbeFromWorkOrigin = true, ZProbeOffsetX = 1, ZProbeOffsetY = 2 };
        options.ZProbeAtCenterOf(Program);

        Assert.False(options.ZProbeFromWorkOrigin);
        Assert.Equal((50d, 30d), (options.ZProbeOffsetX, options.ZProbeOffsetY));
        Assert.Equal((60d, 50d), options.ZProbePoint(Program));
        Assert.Equal("M495 X10 Y20 O50 F30", JobStartPlan.Automation(options, Program));
        Assert.True(options.ZProbeWithin(Program));

        options.ZProbeOffsetY = 61;
        Assert.False(options.ZProbeWithin(Program));
    }

    [Fact]
    public void Switching_what_the_z_probe_offset_counts_from_keeps_the_point()
    {
        var options = new JobStartOptions { ZProbe = true, ZProbeOffsetX = 50, ZProbeOffsetY = 30 };

        options.SetZProbeFromWorkOrigin(true, Program);
        Assert.Equal((60d, 50d), (options.ZProbeOffsetX, options.ZProbeOffsetY));
        Assert.Equal("M495 X10 Y20 O50 F30", JobStartPlan.Automation(options, Program));

        options.SetZProbeFromWorkOrigin(false, Program);
        Assert.Equal((50d, 30d), (options.ZProbeOffsetX, options.ZProbeOffsetY));

        options.SetZProbeFromWorkOrigin(true, null);
        Assert.True(options.ZProbeFromWorkOrigin);
        Assert.Equal((50d, 30d), (options.ZProbeOffsetX, options.ZProbeOffsetY));
    }

    [Fact]
    public void Plan_sends_modes_and_origin_now_and_queues_probing_and_the_tool_before_play()
    {
        var options = new JobStartOptions
        {
            WorkOrigin = WorkOriginChoice.BedCorner, OriginOffsetX = 10, OriginOffsetY = 5, ZProbe = true,
            Modes = { ["blow"] = true },
        };
        var plan = JobStartPlan.Create(options, Context(tool: 3), "/sd/gcodes/my part.nc");

        Assert.Equal(
        [
            "M331.1", "M332.2", "M332.4", "M332.3",
            "G10 L2 P0 X-180.3 Y-189.114",
            "buffer M495 X10 Y20 O0 F0",
            "buffer M6 T3",
            "play /sd/gcodes/my\u0001part.nc",
        ], plan.Commands);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9999)]
    [InlineData(-1)]
    public void No_tool_is_changed_back_when_none_was_cutting(int tool)
    {
        var plan = JobStartPlan.Create(new JobStartOptions { Leveling = true }, Context(tool), "/sd/gcodes/a.nc");
        Assert.DoesNotContain(plan.Commands, c => c.StartsWith("buffer M6", StringComparison.Ordinal));
    }

    [Fact]
    public void No_tool_is_changed_back_when_the_program_changes_to_its_own()
    {
        var plan = JobStartPlan.Create(new JobStartOptions { ZProbe = true }, Context(tool: 3) with { ProgramSelectsTool = true }, "/sd/gcodes/a.nc");
        Assert.DoesNotContain(plan.Commands, c => c.StartsWith("buffer M6", StringComparison.Ordinal));
    }

    [Fact]
    public void The_probing_area_leaves_out_rapids_to_a_safe_height_and_a_park_position()
    {
        var toolpath = GCodeEstimator.Analyze(string.Join('\n',
            "G21 G90", "G0 Z5", "G0 X10 Y20", "G1 Z-1 F300", "G1 X60 F1000", "G1 Y40", "G0 Z5", "G0 X0 Y0")).Toolpath;

        var cut = toolpath.CutBounds()!;
        Assert.Equal((10, 20, 60, 40), (cut.MinX, cut.MinY, cut.MaxX, cut.MaxY));
        Assert.Equal(0, toolpath.Bounds()!.MinX); // the whole path includes the park move
        Assert.Null(GCodeEstimator.Analyze("G0 X5 Y5\nG0 Z10").Toolpath.CutBounds());
    }

    [Fact]
    public void Without_probing_no_tool_is_changed()
    {
        var plan = JobStartPlan.Create(new JobStartOptions(), Context(tool: 2), "/sd/gcodes/a.nc");
        Assert.Equal(["buffer M495 X10 Y20 P1", "play /sd/gcodes/a.nc"], plan.Commands.Where(c => !c.StartsWith('M')));
    }

    [Fact]
    public void The_xyz_probe_runs_before_the_other_automation()
    {
        var options = new JobStartOptions { WorkOrigin = WorkOriginChoice.XyzProbe, XyzProbeHeight = 9, XyzProbeToolDiameter = 3.175 };
        var plan = JobStartPlan.Create(options, Context(), "/sd/gcodes/a.nc");
        Assert.Equal(["buffer M495.3 H9 D3.175", "buffer M495 X10 Y20 P1"], plan.Commands.Where(c => c.StartsWith("buffer", StringComparison.Ordinal)));
    }

    [Fact]
    public void Head_position_origin_adds_the_offset_to_the_machine_position()
    {
        var options = new JobStartOptions { WorkOrigin = WorkOriginChoice.HeadPosition, OriginOffsetX = -2.5 };
        Assert.Contains("G10 L2 P0 X-102.5 Y-120", JobStartPlan.Create(options, Context(), "/sd/gcodes/a.nc").Commands);
    }

    [Fact]
    public void Other_machines_get_the_vacuum_and_external_output_modes()
    {
        var large = new MachineTravel(new AxisRange(-371, 0), new AxisRange(-250, 0), new AxisRange(-135, 0), null, null, -3);
        var plan = JobStartPlan.Create(new JobStartOptions { Modes = { ["vacuum"] = true } }, Context(travel: large), "/sd/gcodes/a.nc");
        Assert.Equal(["M331", "M332.3"], plan.Commands.Take(2));
    }

    [Fact]
    public void Invalid_options_are_refused()
    {
        Assert.Throws<JobStartException>(() =>
            JobStartPlan.Create(new JobStartOptions { Leveling = true, LevelingPointsX = 1 }, Context(), "/sd/gcodes/a.nc"));
        Assert.Throws<JobStartException>(() =>
            JobStartPlan.Create(new JobStartOptions { WorkOrigin = WorkOriginChoice.BedCorner },
                Context(travel: CompactTravel with { BedCornerX = null }), "/sd/gcodes/a.nc"));
        Assert.Throws<JobStartException>(() =>
            JobStartPlan.Create(new JobStartOptions { ZProbe = true }, Context() with { Program = null }, "/sd/gcodes/a.nc"));
    }
}

public class RemoteFileTests
{
    [Fact]
    public void Listing_lines_give_name_size_date_and_directories()
    {
        var entries = RemoteListing.Parse("my\u0001part.nc 1234 20261105143000\r\nold/ 0 20250101000000\r\nodd.nc 5 20261305000000\r\ngarbage\r\n");

        Assert.Equal(3, entries.Count);
        Assert.Equal(new RemoteEntry("my part.nc", false, 1234, new DateTime(2026, 11, 5, 14, 30, 0)), entries[0]);
        Assert.Equal(new RemoteEntry("old", true, 0, new DateTime(2025, 1, 1)), entries[1]);
        Assert.Null(entries[2].Modified); // month 13: no valid date
    }

    [Fact]
    public void Paths_in_commands_encode_spaces()
    {
        Assert.Equal("ls -e -s /sd/gcodes/a\u0001b", RemoteListing.Command("/sd/gcodes/a b"));
        Assert.Equal("play /sd/gcodes/a\u0001b\u0002\u0003\u0004\u0005.nc", JobCommands.Play("/sd/gcodes/a b?*!~.nc"));
        Assert.Equal("a b?*!~.nc", RemotePath.Decode(RemotePath.Encode("a b?*!~.nc")));
        Assert.Equal("upload /sd/gcodes/a\u0001b.nc", FileTransferCommand.Upload("/sd/gcodes/a b.nc").Text);
        Assert.Equal("/sd/gcodes/part.nc", RemotePath.ForProgram("part.nc"));
    }

    [Fact]
    public async Task Upload_answers_the_machines_requests_until_it_confirms()
    {
        var content = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(0, 400).Select(i => $"G1 X{i}\n")));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var receiver = Task.Run(() => FakeUploadMachine.ReceiveAsync(listener, cancel: false, loseFirstMd5: true));

        await using var machine = MachineConnection.ForAddress($"127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}");
        machine.StatusPollInterval = null;
        await machine.ConnectAsync();
        var progress = new List<UploadProgress>();
        await new FileUploader(machine) { PacketSize = 1000, Md5Resend = TimeSpan.FromMilliseconds(300) }.UploadAsync("/sd/gcodes/test part.nc", content, new SyncProgress(progress.Add));

        var received = await receiver.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("upload /sd/gcodes/test\u0001part.nc", received.Command);
        Assert.Equal(Convert.ToHexString(MD5.HashData(content)).ToLowerInvariant(), received.Md5);
        Assert.Equal(new FileView(4, 1000), received.View);
        Assert.Equal(content, received.Content);
        Assert.Equal([1, 1, 2, 3, 4], progress.Select(p => p.Packet)); // packet 1 was asked for twice
        Assert.Equal(content.Length, progress[^1].Bytes);
        listener.Stop();
    }

    [Fact]
    public async Task An_upload_fails_when_the_machine_stops_asking()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var receiver = Task.Run(() => FakeUploadMachine.ReceiveAsync(listener, cancel: false, silentAfterView: true));

        await using var machine = MachineConnection.ForAddress($"127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}");
        machine.StatusPollInterval = null;
        await machine.ConnectAsync();

        var uploader = new FileUploader(machine) { RequestTimeout = TimeSpan.FromMilliseconds(200), MaxRetries = 1 };
        var error = await Assert.ThrowsAsync<FileTransferException>(() => uploader.UploadAsync("/sd/gcodes/a.nc", [1, 2, 3]));
        Assert.Contains("stopped asking", error.Message);
        Assert.True((await receiver.WaitAsync(TimeSpan.FromSeconds(5))).Cancelled); // the machine is told to stop waiting
        listener.Stop();
    }

    [Fact]
    public async Task Listing_collects_every_packet_until_the_end()
    {
        var (listener, server) = FakeListingMachine.Start(
            Packet.FromText(PacketType.LoadInfo, "a.nc 10 20261105143000\r\n"),
            Packet.FromText(PacketType.LoadInfo, "b\u0004.nc 20 20261105143000\r\nold/ 0 20250101000000\r\n"),
            Packet.FromText(PacketType.LoadFinished, "Load directory finished.\r\n"));
        await using var machine = await FakeListingMachine.ConnectAsync(listener);

        var entries = await new RemoteDirectoryReader(machine).ListAsync("/sd/gcodes");

        Assert.Equal(["a.nc", "b!.nc", "old"], entries.Select(e => e.Name));
        Assert.Equal("ls -e -s /sd/gcodes", await server);
        listener.Stop();
    }

    [Fact]
    public async Task Listing_a_missing_directory_fails()
    {
        var (listener, _) = FakeListingMachine.Start(Packet.FromText(PacketType.LoadError, "Could not open directory!\r\n"));
        await using var machine = await FakeListingMachine.ConnectAsync(listener);

        var error = await Assert.ThrowsAsync<FileTransferException>(() => new RemoteDirectoryReader(machine).ListAsync("/sd/nope"));
        Assert.Equal("Could not open directory!", error.Message);
        listener.Stop();
    }

    /// <summary>Answers the first text command with the given packets and returns that command.</summary>
    private static class FakeListingMachine
    {
        public static (TcpListener, Task<string>) Start(params Packet[] replies)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return (listener, Task.Run(async () =>
            {
                using var client = await listener.AcceptTcpClientAsync();
                var reader = new PacketReader();
                var buffer = new byte[4096];
                while (true)
                {
                    var n = await client.GetStream().ReadAsync(buffer);
                    if (reader.Feed(buffer.AsSpan(0, n)).Select(IHostMessage.From).OfType<TextCommand>().FirstOrDefault() is not { } command)
                        continue;
                    foreach (var reply in replies) await client.GetStream().WriteAsync(reply.Encode());
                    await Task.Delay(200);
                    return command.Text;
                }
            }));
        }

        public static async Task<MachineConnection> ConnectAsync(TcpListener listener)
        {
            var machine = MachineConnection.ForAddress($"127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}");
            machine.StatusPollInterval = null;
            await machine.ConnectAsync();
            return machine;
        }
    }

    [Fact]
    public async Task An_upload_the_machine_cancels_fails()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        _ = Task.Run(() => FakeUploadMachine.ReceiveAsync(listener, cancel: true));

        await using var machine = MachineConnection.ForAddress($"127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}");
        machine.StatusPollInterval = null;
        await machine.ConnectAsync();

        await Assert.ThrowsAsync<FileTransferException>(() => new FileUploader(machine).UploadAsync("/sd/gcodes/a.nc", [1, 2, 3]));
        listener.Stop();
    }

    private sealed class SyncProgress(Action<UploadProgress> report) : IProgress<UploadProgress>
    {
        public void Report(UploadProgress value) => report(value);
    }

    private sealed record Received(string Command, string Md5, FileView View, byte[] Content, bool Cancelled);

    /// <summary>
    /// Machine side of an upload as the firmware does it; asks for the first packet twice. It can cancel right away,
    /// lose the first MD5 (as when the firmware empties its receive buffer) or go silent after the view.
    /// </summary>
    private sealed class FakeUploadMachine(Stream stream, bool cancel, bool loseFirstMd5, bool silentAfterView) : IHostMessageVisitor
    {
        private readonly MemoryStream _content = new();
        private string _command = "", _md5 = "";
        private FileView _view = FileView.Request;
        private int _next = 1;
        private bool _repeated;
        private bool _lostMd5;
        private bool _cancelled;

        public bool Done { get; private set; }

        public static async Task<Received> ReceiveAsync(
            TcpListener listener, bool cancel, bool loseFirstMd5 = false, bool silentAfterView = false)
        {
            using var client = await listener.AcceptTcpClientAsync();
            var machine = new FakeUploadMachine(client.GetStream(), cancel, loseFirstMd5, silentAfterView);
            var reader = new PacketReader();
            var buffer = new byte[16384];
            while (!machine.Done)
            {
                var n = await client.GetStream().ReadAsync(buffer);
                if (n == 0) break;
                foreach (var packet in reader.Feed(buffer.AsSpan(0, n)))
                    await IHostMessage.From(packet).AcceptAsync(machine);
            }
            return new Received(machine._command, machine._md5, machine._view, machine._content.ToArray(), machine._cancelled);
        }

        private Task SendAsync(IHostMessage message) => stream.WriteAsync(message.ToPacket().Encode()).AsTask();

        public Task VisitAsync(FileTransferCommand message)
        {
            _command = message.Text;
            return cancel ? Finish(new FileCancel()) : Task.CompletedTask;
        }

        public Task VisitAsync(FileMd5 message)
        {
            if (loseFirstMd5 && !_lostMd5)
            {
                _lostMd5 = true;
                return Task.CompletedTask;
            }
            _md5 = message.Md5;
            return SendAsync(FileView.Request);
        }

        public Task VisitAsync(FileView message)
        {
            _view = message;
            return silentAfterView ? Task.CompletedTask : SendAsync(FileData.RequestFor(1));
        }

        public Task VisitAsync(FileData message)
        {
            if (message.Sequence == 1 && !_repeated)
            {
                _repeated = true;
                return SendAsync(FileData.RequestFor(1));
            }
            if (message.Sequence != _next) return SendAsync(FileData.RequestFor(_next));
            _content.Write(message.Data);
            return ++_next > _view.TotalPackets ? Finish(new FileEnd()) : SendAsync(FileData.RequestFor(_next));
        }

        private Task Finish(IHostMessage last)
        {
            Done = true;
            return SendAsync(last);
        }

        public Task VisitAsync(RealtimeCommand message) => Task.CompletedTask;
        public Task VisitAsync(TextCommand message) => Task.CompletedTask;
        public Task VisitAsync(FileQueryRequest message) => Task.CompletedTask;
        public Task VisitAsync(FileEnd message) => Task.CompletedTask;
        public Task VisitAsync(FileCancel message)
        {
            _cancelled = Done = true;
            return Task.CompletedTask;
        }
        public Task VisitAsync(FileRetry message) => Task.CompletedTask;
        public Task VisitAsync(UnknownHostMessage message) => Task.CompletedTask;
    }
}

public class JobOptionsStoreTests
{
    private sealed class Env(string root) : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = root;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    [Fact]
    public void Saved_options_are_read_back_per_machine_and_copies_stay_independent()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tinkercnc-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["TinkerCNC:DataDirectory"] = dir }).Build();
            var paths = new TinkerCNC.Host.AppPaths(config, new Env(dir));
            var log = Microsoft.Extensions.Logging.Abstractions.NullLogger<TinkerCNC.Host.JobOptionsStore>.Instance;
            var options = new JobStartOptions
            {
                WorkOrigin = WorkOriginChoice.XyzProbe, XyzProbeHeight = 12.5, Leveling = true, LevelingPointsX = 7,
                Timelapse = true, Modes = { ["blow"] = true },
            };

            new TinkerCNC.Host.JobOptionsStore(paths, log).Save("10.0.0.5:2222", options);
            options.Modes["blow"] = false; // the store keeps its own copy
            var store = new TinkerCNC.Host.JobOptionsStore(paths, log);
            var read = store.Get("10.0.0.5:2222");

            Assert.Equal(WorkOriginChoice.XyzProbe, read.WorkOrigin);
            Assert.Equal(12.5, read.XyzProbeHeight);
            Assert.Equal(7, read.LevelingPointsX);
            Assert.True(read.Leveling);
            Assert.True(read.Modes["blow"]);
            Assert.DoesNotContain("UsesProbe", File.ReadAllText(paths.JobOptions));
            Assert.Equal(WorkOriginChoice.Keep, store.Get("other").WorkOrigin);
            read.Modes["blow"] = false;
            Assert.True(store.Get("10.0.0.5:2222").Modes["blow"]);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
