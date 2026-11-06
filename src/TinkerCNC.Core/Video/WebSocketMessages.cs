using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;

namespace TinkerCNC.Video;

/// <summary>A complete (reassembled) WebSocket message.</summary>
public interface IWebSocketMessage
{
    private static readonly IReadOnlyDictionary<WebSocketMessageType, Func<byte[], IWebSocketMessage>> Factories =
        new Dictionary<WebSocketMessageType, Func<byte[], IWebSocketMessage>>
        {
            [WebSocketMessageType.Binary] = p => new BinaryWebSocketMessage(p),
            [WebSocketMessageType.Text] = p => new TextWebSocketMessage(Encoding.UTF8.GetString(p)),
        };

    Task AcceptAsync(IWebSocketMessageVisitor visitor);

    static IWebSocketMessage Create(WebSocketMessageType type, byte[] payload) => Factories[type](payload);
}

/// <summary>Binary message; on the video channels this is one encoded image (JPEG).</summary>
public sealed record BinaryWebSocketMessage(byte[] Payload) : IWebSocketMessage
{
    public Task AcceptAsync(IWebSocketMessageVisitor visitor) => visitor.VisitAsync(this);
}

public sealed record TextWebSocketMessage(string Text) : IWebSocketMessage
{
    public Task AcceptAsync(IWebSocketMessageVisitor visitor) => visitor.VisitAsync(this);
}

public interface IWebSocketMessageVisitor
{
    Task VisitAsync(BinaryWebSocketMessage message);
    Task VisitAsync(TextWebSocketMessage message);
}

public static class WebSocketMessages
{
    /// <summary>Yields complete messages until the socket closes.</summary>
    public static async IAsyncEnumerable<IWebSocketMessage> ReadAsync(WebSocket ws, [EnumeratorCancellation] CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        while (ws.State == WebSocketState.Open)
        {
            var result = await ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
                yield break;

            message.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage) continue;

            yield return IWebSocketMessage.Create(result.MessageType, message.ToArray());
            message.SetLength(0);
        }
    }
}
