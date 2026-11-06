using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;

namespace TinkerCNC.Discovery;

/// <summary>A machine announced on the LAN.</summary>
/// <param name="Name">Machine name (the firmware's <c>wifi.machine_name</c>, e.g. "Workshop_CNC").</param>
/// <param name="Address">IP address the machine reported.</param>
/// <param name="Port">TCP control port the machine reported (normally 2222).</param>
/// <param name="Busy">True when field 4 equals "1" (shown as "[Busy]"; another client holds the machine).</param>
/// <param name="Extra">Optional 5th field, passed through as-is.</param>
/// <param name="Sender">Source address of the datagram.</param>
public sealed record DiscoveredMachine(string Name, string Address, int Port, bool Busy, string? Extra, IPEndPoint Sender)
{
    public string ConnectAddress => $"{Address}:{Port}";

    public override string ToString() => $"{Name,-30} {ConnectAddress,-22} [{(Busy ? "Busy" : "Available")}] {Extra}".TrimEnd();
}

/// <summary>
/// UDP discovery on port 3333. Machines broadcast comma-separated datagrams "name,ip,port,busy[,extra]"
/// (at least four fields). A specific host can also be asked directly: unicast the ASCII payload "QUERY" and it
/// replies in the same format.
/// </summary>
public static class MachineDiscovery
{
    public const int DiscoveryPort = 3333;
    public const string QueryPayload = "QUERY";

    public static bool TryParse(string datagram, IPEndPoint sender, out DiscoveredMachine? machine)
    {
        machine = null;
        var fields = datagram.Trim().Split(',');
        if (fields.Length < 4 || !int.TryParse(fields[2], out var port))
            return false;
        machine = new DiscoveredMachine(
            fields[0].Trim(), fields[1].Trim(), port, fields[3].Trim() == "1",
            fields.Length > 4 ? fields[4].Trim() : null, sender);
        return true;
    }

    /// <summary>Listens for announcements and yields each machine once (first sighting).</summary>
    public static async IAsyncEnumerable<DiscoveredMachine> ListenAsync(
        TimeSpan duration, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var udp = CreateSocket();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(duration);
        var seen = new ConcurrentDictionary<string, byte>();

        while (!cts.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await udp.ReceiveAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }

            var text = Encoding.UTF8.GetString(result.Buffer);
            if (TryParse(text, result.RemoteEndPoint, out var m) && seen.TryAdd(m!.Name + "|" + m.Address, 0))
                yield return m;
        }
    }

    public static async Task<IReadOnlyList<DiscoveredMachine>> ScanAsync(TimeSpan duration, CancellationToken cancellationToken = default)
    {
        var list = new List<DiscoveredMachine>();
        await foreach (var m in ListenAsync(duration, cancellationToken).ConfigureAwait(false))
            list.Add(m);
        return list;
    }

    /// <summary>
    /// Sends "QUERY" to <paramref name="host"/> and returns the first valid reply.
    /// The destination port is assumed to be 3333, the discovery port.
    /// </summary>
    public static async Task<DiscoveredMachine?> QueryAsync(
        string host, TimeSpan timeout, int port = DiscoveryPort, CancellationToken cancellationToken = default)
    {
        using var udp = CreateSocket();
        var payload = Encoding.ASCII.GetBytes(QueryPayload);
        await udp.SendAsync(payload, new IPEndPoint(IPAddress.Parse(host), port), cancellationToken).ConfigureAwait(false);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            while (true)
            {
                var result = await udp.ReceiveAsync(cts.Token).ConfigureAwait(false);
                if (TryParse(Encoding.UTF8.GetString(result.Buffer), result.RemoteEndPoint, out var m))
                    return m;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private static UdpClient CreateSocket()
    {
        // Share the port with other apps listening for the same broadcasts (other control software).
        var udp = new UdpClient { ExclusiveAddressUse = false, EnableBroadcast = true };
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
        return udp;
    }
}
