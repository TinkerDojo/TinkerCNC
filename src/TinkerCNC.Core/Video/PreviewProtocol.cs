using System.Text.Json;
using System.Text.Json.Serialization;

namespace TinkerCNC.Video;

/// <summary>
/// JSON contracts of the time-lapse preview channel (<c>ws://&lt;ip&gt;:82/ws_preview</c>).
/// Requests are discriminated by <c>cmd</c>, replies by <c>rsp</c>; errors carry <c>err</c> and
/// <c>message</c>/<c>msg</c> instead.
/// </summary>
public static class PreviewJson
{
    public const string Namespace = "vpreview";

    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize(object message) => JsonSerializer.Serialize(message, message.GetType(), Options);

    internal static T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options)!;

    /// <summary>The fields needed to pick the concrete contract.</summary>
    internal sealed record Envelope(string? Cmd, string? Rsp, int? Err);
}

// ------------------------------------------------------------------ requests (client -> machine)

public interface IPreviewRequest
{
    private static readonly IReadOnlyDictionary<string, Func<string, IPreviewRequest>> Parsers =
        new Dictionary<string, Func<string, IPreviewRequest>>
        {
            [OpenRequest.Name] = PreviewJson.Deserialize<OpenRequest>,
            [PlayRequest.Name] = PreviewJson.Deserialize<PlayRequest>,
            [PauseRequest.Name] = PreviewJson.Deserialize<PauseRequest>,
            [ResumeRequest.Name] = PreviewJson.Deserialize<ResumeRequest>,
            [StopRequest.Name] = PreviewJson.Deserialize<StopRequest>,
        };

    string Ns { get; }
    string Cmd { get; }
    int Seq { get; }

    Task AcceptAsync(IPreviewRequestVisitor visitor);

    /// <summary>Parses a request (machine side). Returns null for unknown commands.</summary>
    static IPreviewRequest? Parse(string json) =>
        PreviewJson.Deserialize<PreviewJson.Envelope>(json).Cmd is { } cmd && Parsers.TryGetValue(cmd, out var parse)
            ? parse(json)
            : null;
}

/// <summary>Fields shared by every request: the "vpreview" namespace and a sequence number.</summary>
public abstract record PreviewRequest : IPreviewRequest
{
    public string Ns { get; init; } = PreviewJson.Namespace;
    public abstract string Cmd { get; }
    public int Seq { get; init; }
    public abstract Task AcceptAsync(IPreviewRequestVisitor visitor);
}

public sealed record OpenRequest(string Path) : PreviewRequest
{
    public const string Name = "open";
    public override string Cmd => Name;
    public override Task AcceptAsync(IPreviewRequestVisitor visitor) => visitor.VisitAsync(this);
}

public sealed record PlayRequest(string SessionId, int FromFrame) : PreviewRequest
{
    public const string Name = "play";
    public override string Cmd => Name;
    public override Task AcceptAsync(IPreviewRequestVisitor visitor) => visitor.VisitAsync(this);
}

public sealed record PauseRequest(string SessionId) : PreviewRequest
{
    public const string Name = "pause";
    public override string Cmd => Name;
    public override Task AcceptAsync(IPreviewRequestVisitor visitor) => visitor.VisitAsync(this);
}

public sealed record ResumeRequest(string SessionId) : PreviewRequest
{
    public const string Name = "resume";
    public override string Cmd => Name;
    public override Task AcceptAsync(IPreviewRequestVisitor visitor) => visitor.VisitAsync(this);
}

public sealed record StopRequest(string SessionId) : PreviewRequest
{
    public const string Name = "stop";
    public override string Cmd => Name;
    public override Task AcceptAsync(IPreviewRequestVisitor visitor) => visitor.VisitAsync(this);
}

public interface IPreviewRequestVisitor
{
    Task VisitAsync(OpenRequest request);
    Task VisitAsync(PlayRequest request);
    Task VisitAsync(PauseRequest request);
    Task VisitAsync(ResumeRequest request);
    Task VisitAsync(StopRequest request);
}

// ------------------------------------------------------------------ replies (machine -> client)

public interface IPreviewReply
{
    private static readonly IReadOnlyDictionary<string, Func<string, IPreviewReply>> Parsers =
        new Dictionary<string, Func<string, IPreviewReply>>
        {
            [OpenReply.Name] = PreviewJson.Deserialize<OpenReply>,
            [MetaReply.Name] = PreviewJson.Deserialize<MetaReply>,
            [PlayReply.Name] = PreviewJson.Deserialize<PlayReply>,
            [PauseReply.Name] = PreviewJson.Deserialize<PauseReply>,
            [EosReply.Name] = PreviewJson.Deserialize<EosReply>,
            [PreemptedReply.Name] = PreviewJson.Deserialize<PreemptedReply>,
        };

    string? Rsp { get; }

    Task AcceptAsync(IPreviewReplyVisitor visitor);

    /// <summary>
    /// Parses a reply (client side). A non-zero <c>err</c> takes precedence over <c>rsp</c>.
    /// </summary>
    static IPreviewReply Parse(string json)
    {
        var envelope = PreviewJson.Deserialize<PreviewJson.Envelope>(json);
        if (envelope.Err is { } err && err != 0)
            return PreviewJson.Deserialize<ErrorReply>(json);
        return envelope.Rsp is { } rsp && Parsers.TryGetValue(rsp, out var parse) ? parse(json) : new UnknownReply(json);
    }
}

public sealed record OpenReply(string SessionId, int TotalFrames, int Width, int Height) : IPreviewReply
{
    public const string Name = "open";
    public string Rsp => Name;
    public Task AcceptAsync(IPreviewReplyVisitor visitor) => visitor.VisitAsync(this);
}

public sealed record MetaReply(string? SessionId, int TotalFrames, double FramePeriodUs, double DurationMs, int FirstFrameIndex) : IPreviewReply
{
    public const string Name = "meta";
    public string Rsp => Name;
    public Task AcceptAsync(IPreviewReplyVisitor visitor) => visitor.VisitAsync(this);
}

public sealed record PlayReply(string? SessionId, int? FromFrame) : IPreviewReply
{
    public const string Name = "play";
    public string Rsp => Name;
    public Task AcceptAsync(IPreviewReplyVisitor visitor) => visitor.VisitAsync(this);
}

public sealed record PauseReply(string? SessionId) : IPreviewReply
{
    public const string Name = "pause";
    public string Rsp => Name;
    public Task AcceptAsync(IPreviewReplyVisitor visitor) => visitor.VisitAsync(this);
}

/// <summary>End of stream.</summary>
public sealed record EosReply(string? SessionId) : IPreviewReply
{
    public const string Name = "eos";
    public string Rsp => Name;
    public Task AcceptAsync(IPreviewReplyVisitor visitor) => visitor.VisitAsync(this);
}

/// <summary>Another viewer took over the channel.</summary>
public sealed record PreemptedReply(string? SessionId) : IPreviewReply
{
    public const string Name = "preempted";
    public string Rsp => Name;
    public Task AcceptAsync(IPreviewReplyVisitor visitor) => visitor.VisitAsync(this);
}

public sealed record ErrorReply(int Err, string? Message, string? Msg) : IPreviewReply
{
    public string? Rsp => null;
    [JsonIgnore] public string Text => Message ?? Msg ?? $"Unknown server error ({Err})";
    public Task AcceptAsync(IPreviewReplyVisitor visitor) => visitor.VisitAsync(this);
}

public sealed record UnknownReply(string Json) : IPreviewReply
{
    public string? Rsp => null;
    public Task AcceptAsync(IPreviewReplyVisitor visitor) => visitor.VisitAsync(this);
}

public interface IPreviewReplyVisitor
{
    Task VisitAsync(OpenReply reply);
    Task VisitAsync(MetaReply reply);
    Task VisitAsync(PlayReply reply);
    Task VisitAsync(PauseReply reply);
    Task VisitAsync(EosReply reply);
    Task VisitAsync(PreemptedReply reply);
    Task VisitAsync(ErrorReply reply);
    Task VisitAsync(UnknownReply reply);
}
