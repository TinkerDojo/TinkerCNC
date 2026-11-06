using System.Net.Sockets;

namespace TinkerCNC.Transport;

/// <summary>
/// Wi-Fi/LAN connection. The address is "ip" or "ip:port"; without a port the machine's control port 2222 is used.
/// </summary>
public sealed class TcpTransport : IMachineTransport
{
    public const int DefaultPort = 2222;

    private TcpClient? _client;

    public TcpTransport(string host, int port = DefaultPort)
    {
        Host = host;
        Port = port;
    }

    public string Host { get; }
    public int Port { get; }
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public string Description => $"WiFi {Host}:{Port}";

    public static TcpTransport Parse(string address)
    {
        var parts = address.Trim().Split(':');
        if (parts.Length == 2 && int.TryParse(parts[1], out var port) && port > 0)
            return new TcpTransport(parts[0], port);
        if (parts.Length == 1 && parts[0].Length > 0)
            return new TcpTransport(parts[0]);
        throw new FormatException($"Invalid address format: {address}");
    }

    public async Task<Stream> OpenAsync(CancellationToken cancellationToken = default)
    {
        _client = new TcpClient { NoDelay = true };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ConnectTimeout);
        await _client.ConnectAsync(Host, Port, timeout.Token).ConfigureAwait(false);
        return _client.GetStream();
    }

    public ValueTask DisposeAsync()
    {
        _client?.Dispose();
        _client = null;
        return ValueTask.CompletedTask;
    }
}
