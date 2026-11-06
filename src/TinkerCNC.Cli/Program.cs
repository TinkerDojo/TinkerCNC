using TinkerCNC.Cli.Commands;

ICliCommand[] commands =
{
    new DiscoverCommand(),
    new QueryCommand(),
    new ConnectCommand(),
    new ConnectByNameCommand(),
    new SerialCommand(),
    new PortsCommand(),
};

var byName = commands.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);

if (args.Length == 0 || !byName.TryGetValue(args[0], out var command))
{
    Console.WriteLine("usage:");
    foreach (var c in commands) Console.WriteLine($"  tinkercnc-cli {c.Name} {c.Usage}");
    return 1;
}

try
{
    return await command.RunAsync(new CliArguments(args.Skip(1).ToArray()));
}
catch (Exception ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 2;
}
