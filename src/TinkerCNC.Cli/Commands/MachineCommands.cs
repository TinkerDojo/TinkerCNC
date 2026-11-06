using TinkerCNC.Discovery;
using TinkerCNC.Transport;

namespace TinkerCNC.Cli.Commands;

/// <summary>Listens for UDP announcements on port 3333.</summary>
public sealed class DiscoverCommand : ICliCommand
{
    public string Name => "discover";
    public string Usage => "[seconds]";

    public async Task<int> RunAsync(CliArguments args)
    {
        var seconds = int.Parse(args.Optional(0) ?? "5");
        Console.WriteLine($"listening on UDP {MachineDiscovery.DiscoveryPort} for {seconds}s...");
        await foreach (var m in MachineDiscovery.ListenAsync(TimeSpan.FromSeconds(seconds)))
            Console.WriteLine($"  {m}");
        return 0;
    }
}

/// <summary>Unicasts "QUERY" to one address.</summary>
public sealed class QueryCommand : ICliCommand
{
    public string Name => "query";
    public string Usage => "<ip>";

    public async Task<int> RunAsync(CliArguments args)
    {
        var m = await MachineDiscovery.QueryAsync(args.Required(0, "ip"), TimeSpan.FromSeconds(3));
        Console.WriteLine(m?.ToString() ?? "no reply");
        return m is null ? 1 : 0;
    }
}

/// <summary>Interactive session over Wi-Fi/LAN (default port 2222).</summary>
public sealed class ConnectCommand : ICliCommand
{
    public string Name => "connect";
    public string Usage => "<ip[:port]>";

    public Task<int> RunAsync(CliArguments args) =>
        new InteractiveSession(MachineConnection.ForAddress(args.Required(0, "ip[:port]"))).RunAsync();
}

/// <summary>Connects to a machine by the name it announces on the network.</summary>
public sealed class ConnectByNameCommand : ICliCommand
{
    public string Name => "connect-name";
    public string Usage => "<name>";

    public async Task<int> RunAsync(CliArguments args)
    {
        var name = args.Required(0, "name");
        Console.WriteLine("searching the LAN...");
        var found = (await MachineDiscovery.ScanAsync(TimeSpan.FromSeconds(5)))
            .FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"no machine named '{name}' announced itself");
        return await new InteractiveSession(MachineConnection.ForAddress(found.ConnectAddress)).RunAsync();
    }
}

/// <summary>Interactive session over USB-serial (115200 8N1).</summary>
public sealed class SerialCommand : ICliCommand
{
    public string Name => "serial";
    public string Usage => "<port>";

    public Task<int> RunAsync(CliArguments args) =>
        new InteractiveSession(MachineConnection.ForSerialPort(args.Required(0, "port"))).RunAsync();
}

public sealed class PortsCommand : ICliCommand
{
    public string Name => "ports";
    public string Usage => "";

    public Task<int> RunAsync(CliArguments args)
    {
        foreach (var p in SerialTransport.ListPorts()) Console.WriteLine(p);
        return Task.FromResult(0);
    }
}
