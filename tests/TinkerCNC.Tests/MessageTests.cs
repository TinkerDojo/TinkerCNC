using TinkerCNC.Protocol;
using TinkerCNC.Video;

namespace TinkerCNC.Tests;

public class MessageTests
{
    private sealed class Recorder : MachineMessageVisitor
    {
        public readonly List<string> Seen = new();
        public override void Visit(StatusMessage message) => Seen.Add("status:" + message.Report.State);
        public override void Visit(InfoMessage message) => Seen.Add("info:" + message.Text);
        public override void Visit(AlarmMessage message) => Seen.Add("alarm:" + message.Text);
        public override void Visit(UnknownMachineMessage message) => Seen.Add("unknown:" + message.Packet.Type);
    }

    [Fact]
    public void Machine_packets_dispatch_to_typed_messages()
    {
        var recorder = new Recorder();
        Packet[] packets =
        {
            Packet.FromText(PacketType.Status, "<Idle|MPos:0,0,0,0>"),
            Packet.FromText(PacketType.NormalInfo, "ok"),
            Packet.FromText(PacketType.AlarmInfo, "Probe Fail"),
            new((PacketType)0x99, Array.Empty<byte>()),
        };

        foreach (var p in packets) IMachineMessage.From(p).Accept(recorder);

        Assert.Equal(new[] { "status:Idle", "info:ok", "alarm:Probe Fail", "unknown:153" }, recorder.Seen);
    }

    [Fact]
    public void Host_messages_roundtrip_through_packets()
    {
        IHostMessage[] messages = { RealtimeCommand.FeedHold, new TextCommand("G0 X1"), new FileQueryRequest() };
        foreach (var m in messages)
            Assert.Equal(m, IHostMessage.From(m.ToPacket()));
    }

    [Fact]
    public void Download_command_is_sent_without_newline()
    {
        Assert.Equal("download /sd/config.txt", FileTransferCommand.Download("/sd/config.txt").ToPacket().Text);
    }

    [Fact]
    public void Preview_requests_serialize_with_documented_field_names()
    {
        var json = PreviewJson.Serialize(new PlayRequest("s1", 12) { Seq = 3 });
        var parsed = System.Text.Json.JsonDocument.Parse(json).RootElement;

        Assert.Equal("vpreview", parsed.GetProperty("ns").GetString());
        Assert.Equal("play", parsed.GetProperty("cmd").GetString());
        Assert.Equal(3, parsed.GetProperty("seq").GetInt32());
        Assert.Equal("s1", parsed.GetProperty("session_id").GetString());
        Assert.Equal(12, parsed.GetProperty("from_frame").GetInt32());
    }

    [Fact]
    public void Preview_requests_roundtrip()
    {
        IPreviewRequest request = new OpenRequest("/sd/timelapse/a.mjpg") { Seq = 1 };
        Assert.Equal(request, IPreviewRequest.Parse(PreviewJson.Serialize(request)));
    }

    [Fact]
    public void Preview_replies_parse_to_typed_records()
    {
        var meta = IPreviewReply.Parse("""{"ns":"vpreview","rsp":"meta","session_id":"s1","total_frames":150,"frame_period_us":100000,"duration_ms":15000,"first_frame_index":0}""");
        Assert.Equal(new MetaReply("s1", 150, 100000, 15000, 0), meta);

        var error = IPreviewReply.Parse("""{"rsp":"open","err":2,"msg":"file not found"}""");
        Assert.Equal("file not found", Assert.IsType<ErrorReply>(error).Text);

        Assert.IsType<UnknownReply>(IPreviewReply.Parse("""{"rsp":"something-new"}"""));
    }
}
