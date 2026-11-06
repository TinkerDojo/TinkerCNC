using System.Net;
using System.Net.Sockets;
using System.Text;
using TinkerCNC.Discovery;
using TinkerCNC.Transport;

namespace TinkerCNC.Simulator;

/// <summary>Sends "name,ip,port,busy" on UDP 3333 once per second, like the firmware.</summary>
public sealed class Announcer(SimulatorOptions options, SimMachine machine)
{
    private static readonly IPEndPoint[] Targets =
    {
        new(IPAddress.Broadcast, MachineDiscovery.DiscoveryPort),
        new(IPAddress.Loopback, MachineDiscovery.DiscoveryPort),
    };

    public async Task RunAsync(CancellationToken ct)
    {
        using var udp = new UdpClient { EnableBroadcast = true };
        while (!ct.IsCancellationRequested)
        {
            var datagram = Encoding.ASCII.GetBytes($"{options.Name},{options.Ip},{TcpTransport.DefaultPort},{(machine.Busy ? 1 : 0)}");
            foreach (var target in Targets)
            {
                try { await udp.SendAsync(datagram, target, ct); }
                catch (SocketException) { /* e.g. no broadcast route */ }
            }
            await Task.Delay(1000, ct);
        }
    }
}
