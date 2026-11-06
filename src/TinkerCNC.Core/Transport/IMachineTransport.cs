namespace TinkerCNC.Transport;

/// <summary>
/// Byte transport to a machine (Wi-Fi/LAN or USB serial); the framed packet protocol on top is identical for all
/// of them.
/// </summary>
public interface IMachineTransport : IAsyncDisposable
{
    string Description { get; }

    Task<Stream> OpenAsync(CancellationToken cancellationToken = default);
}
