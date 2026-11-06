using TinkerCNC.Discovery;

namespace TinkerCNC.Host;

/// <summary>Listens for machine announcements on UDP 3333 for as long as the app runs.</summary>
public sealed class DiscoveryService : BackgroundService
{
    private readonly MachineDirectory _directory;
    private readonly ILogger<DiscoveryService> _log;

    public DiscoveryService(MachineDirectory directory, ILogger<DiscoveryService> log)
    {
        _directory = directory;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var status = _directory.Discovery;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                status.Listening = true;
                status.Error = null;
                // ListenAsync de-duplicates per call, so restart it regularly to keep "last seen" fresh.
                await foreach (var m in MachineDiscovery.ListenAsync(TimeSpan.FromSeconds(3), stoppingToken))
                {
                    status.Packets++;
                    status.LastPacketUtc = DateTime.UtcNow;
                    _directory.ReportDiscovered(m);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                status.Listening = false;
                status.Error = ex.Message;
                _log.LogWarning(ex, "UDP discovery failed; retrying in 10 s");
                try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
        status.Listening = false;
    }

    /// <summary>Unicast "QUERY" to an address, to check a known device directly.</summary>
    public async Task<DiscoveredMachine?> QueryAsync(string host, CancellationToken ct = default)
    {
        var started = DateTime.UtcNow;
        var m = await MachineDiscovery.QueryAsync(host, TimeSpan.FromSeconds(3), cancellationToken: ct);
        if (m is not null)
        {
            _directory.ReportDiscovered(m);
            return m;
        }

        // Both sockets share port 3333, so the reply may have been delivered to the background
        // listener instead. In that case it is already in the directory.
        var seen = _directory.All.FirstOrDefault(e => e.Host == host && e.LastSeenUtc >= started);
        return seen is null ? null : new DiscoveredMachine(seen.Name, host, Transport.TcpTransport.Parse(seen.Address!).Port,
            seen.Busy, seen.Extra, new System.Net.IPEndPoint(System.Net.IPAddress.Parse(host), MachineDiscovery.DiscoveryPort));
    }
}
