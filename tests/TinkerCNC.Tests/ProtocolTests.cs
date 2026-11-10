using System.Net;
using System.Net.Sockets;
using TinkerCNC;
using TinkerCNC.Discovery;
using TinkerCNC.Protocol;
using TinkerCNC.Transport;

namespace TinkerCNC.Tests;

public class ProtocolTests
{
    [Fact]
    public void Crc_matches_xmodem_check_value()
    {
        // Standard CRC-16/XMODEM check value for "123456789".
        Assert.Equal(0x31C3, Crc16Xmodem.Compute("123456789"u8));
    }

    [Fact]
    public void Encode_produces_documented_layout()
    {
        var frame = new Packet(PacketType.ControlSingle, new[] { (byte)'?' }).Encode();

        // 86 68 | 00 04 | A1 | 3F | crc | 55 AA
        Assert.Equal(10, frame.Length);
        Assert.Equal(new byte[] { 0x86, 0x68, 0x00, 0x04, 0xA1, 0x3F }, frame[..6]);
        var crc = Crc16Xmodem.Compute(new byte[] { 0x00, 0x04, 0xA1, 0x3F });
        Assert.Equal((byte)(crc >> 8), frame[6]);
        Assert.Equal((byte)crc, frame[7]);
        Assert.Equal(new byte[] { 0x55, 0xAA }, frame[8..]);
    }

    [Fact]
    public void Decode_roundtrips_and_rejects_bad_crc()
    {
        var frame = Packet.FromText(PacketType.ControlMulti, "G0 X10").Encode();
        Assert.True(Packet.TryDecode(frame, out var p));
        Assert.Equal(PacketType.ControlMulti, p!.Type);
        Assert.Equal("G0 X10", p.Text);

        frame[6] ^= 0xFF;
        Assert.False(Packet.TryDecode(frame, out _));
    }

    [Fact]
    public void Reader_resyncs_across_garbage_and_split_reads()
    {
        var a = Packet.FromText(PacketType.Status, "<Idle|MPos:1.000,2.000,3.000,0.000|WPos:0,0,0,0>").Encode();
        var b = Packet.FromText(PacketType.NormalInfo, "ok").Encode();
        var stream = new byte[] { 0x00, 0x86, 0x12 }.Concat(a).Concat(new byte[] { 0x55 }).Concat(b).ToArray();

        var reader = new PacketReader();
        var packets = new List<Packet>();
        foreach (var chunk in stream.Chunk(3))
            packets.AddRange(reader.Feed(chunk));

        Assert.Equal(2, packets.Count);
        Assert.Equal(PacketType.Status, packets[0].Type);
        Assert.Equal("ok", packets[1].Text);
    }

    [Fact]
    public void Status_report_is_parsed()
    {
        var s = StatusReport.Parse("<Run|MPos:-10.5,20.25,-3.000,90|WPos:1,2,3,4|F:1200,100|S:10000,100>");
        Assert.Equal("Run", s.State);
        Assert.Equal(new[] { -10.5, 20.25, -3.0, 90 }, s.MachinePosition);
        Assert.Equal(new double[] { 1, 2, 3, 4 }, s.WorkPosition);
        Assert.Equal("1200,100", s.Fields["F"]);
    }

    [Theory]
    [InlineData("192.168.1.20", "192.168.1.20", 2222)]
    [InlineData("192.168.1.20:2223", "192.168.1.20", 2223)]
    public void Tcp_address_parsing(string input, string host, int port)
    {
        var t = TcpTransport.Parse(input);
        Assert.Equal(host, t.Host);
        Assert.Equal(port, t.Port);
    }

    [Fact]
    public void Discovery_datagram_is_parsed()
    {
        var sender = new IPEndPoint(IPAddress.Loopback, 3333);
        Assert.True(MachineDiscovery.TryParse("Workshop_CNC_1234,192.168.1.42,2222,1", sender, out var m));
        Assert.Equal("Workshop_CNC_1234", m!.Name);
        Assert.Equal("192.168.1.42:2222", m.ConnectAddress);
        Assert.True(m.Busy);
        Assert.False(MachineDiscovery.TryParse("garbage", sender, out _));
    }

    [Fact]
    public async Task Machine_talks_to_a_fake_controller_over_tcp()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var received = new List<Packet>();
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var s = client.GetStream();
            var reader = new PacketReader();
            var buf = new byte[1024];
            while (received.Count < 2)
            {
                var n = await s.ReadAsync(buf);
                if (n == 0) break;
                foreach (var p in reader.Feed(buf.AsSpan(0, n)))
                {
                    received.Add(p);
                    var reply = p.Type == PacketType.ControlSingle
                        ? Packet.FromText(PacketType.Status, "<Idle|MPos:0,0,0,0|WPos:0,0,0,0>")
                        : Packet.FromText(PacketType.NormalInfo, "echo: " + p.Text);
                    await s.WriteAsync(reply.Encode());
                }
            }
        });

        await using var machine = MachineConnection.ForAddress($"127.0.0.1:{port}");
        machine.StatusPollInterval = null;
        var status = new TaskCompletionSource<StatusReport>();
        machine.StatusReceived += r => status.TrySetResult(r);

        await machine.ConnectAsync();
        await machine.RequestStatusAsync();
        Assert.Equal("Idle", (await status.Task.WaitAsync(TimeSpan.FromSeconds(5))).State);
        Assert.Equal("echo: version", await machine.QueryAsync("version", TimeSpan.FromSeconds(5)));

        await server.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(PacketType.ControlSingle, received[0].Type);
        Assert.Equal("?", received[0].Text);
        Assert.Equal(PacketType.ControlMulti, received[1].Type);
        listener.Stop();
    }
}

public class ToolTipTests
{
    // A tool 12.5 mm longer than the reference: the firmware reports WPos = MPos − work offset − tool offset.
    private static readonly StatusReport Calibrated = StatusReport.Parse("<Idle|MPos:10.000,20.000,-30.000,0.000|WPos:5.000,5.000,-40.500,0.000|T:2,12.500,-1>");

    [Fact]
    public void The_tip_is_below_the_machine_position_by_the_tool_length_offset()
    {
        Assert.Equal(12.5, Calibrated.ToolLengthOffset);
        Assert.Equal([10, 20, -42.5, 0], Calibrated.ToolTip!);
    }

    [Fact]
    public void Work_zero_leaves_the_tool_length_offset_out()
    {
        // MPos − WPos is 10.5 in Z, of which 12.5 is the tool: the work offset itself is −2.
        Assert.Equal([5, 15, -2], Calibrated.WorkZero!);
        // The tip at work zero plus its work position is where it really is.
        Assert.Equal(Calibrated.ToolTip![2], Calibrated.WorkZero![2] + Calibrated.WorkPosition![2], 6);
    }

    [Fact]
    public void Without_a_tool_offset_the_tip_is_the_machine_position()
    {
        var status = StatusReport.Parse("<Idle|MPos:1.000,2.000,3.000|WPos:0.000,0.000,1.000>");
        Assert.Equal([1, 2, 3], status.ToolTip!);
        Assert.Equal([1, 2, 2], status.WorkZero!);
    }
}
