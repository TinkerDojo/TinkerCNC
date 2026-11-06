using TinkerCNC.Protocol;

namespace TinkerCNC.Cli.Commands;

/// <summary>
/// Console REPL on an open machine connection. Plain lines are sent as text commands (0xA2);
/// lines starting with '/' are local commands.
/// </summary>
public sealed class InteractiveSession
{
    private readonly MachineConnection _machine;
    private readonly ConsolePrinter _printer = new();
    private readonly IReadOnlyDictionary<string, Func<Task>> _localCommands;
    private bool _quit;

    public InteractiveSession(MachineConnection machine)
    {
        _machine = machine;
        _localCommands = new Dictionary<string, Func<Task>>(StringComparer.OrdinalIgnoreCase)
        {
            ["/quit"] = () => { _quit = true; return Task.CompletedTask; },
            ["/status"] = () => { Console.WriteLine(_machine.LastStatus?.Raw ?? "(no status yet)"); return Task.CompletedTask; },
            ["/watch"] = () => { _printer.ShowStatus = !_printer.ShowStatus; return Task.CompletedTask; },
            ["/raw"] = () => { _printer.ShowRaw = !_printer.ShowRaw; return Task.CompletedTask; },
            ["/hold"] = () => _machine.SendRealtimeAsync(RealtimeCommand.FeedHold),
            ["/resume"] = () => _machine.SendRealtimeAsync(RealtimeCommand.CycleStart),
            ["/reset"] = () => _machine.SendRealtimeAsync(RealtimeCommand.Reset),
        };
    }

    public async Task<int> RunAsync()
    {
        _machine.PacketReceived += _printer.PrintRaw;
        _machine.MessageReceived += m => m.Accept(_printer);
        _machine.Disconnected += ex => Console.WriteLine($"[disconnected] {ex?.Message}");

        Console.WriteLine($"connecting to {_machine.Description}...");
        await _machine.ConnectAsync();
        Console.WriteLine($"""
            connected. Lines are sent as commands (0xA2). Local commands:
              {string.Join("  ", _localCommands.Keys)}
            """);

        while (!_quit && _machine.IsConnected && Console.ReadLine() is { } line)
            await HandleLineAsync(line.Trim());

        await _machine.DisposeAsync();
        return 0;
    }

    private Task HandleLineAsync(string line)
    {
        if (line.Length == 0) return Task.CompletedTask;
        return _localCommands.GetValueOrDefault(line, () => _machine.SendCommandAsync(line))();
    }

    /// <summary>Prints incoming messages to the console.</summary>
    private sealed class ConsolePrinter : MachineMessageVisitor
    {
        public bool ShowStatus { get; set; }
        public bool ShowRaw { get; set; }

        public void PrintRaw(Packet packet)
        {
            if (ShowRaw) Console.WriteLine($"[rx] {packet} {MachineConnection.Hex(packet.Data)}");
        }

        public override void Visit(StatusMessage message)
        {
            if (ShowStatus) Console.WriteLine($"[status] {message.Report}");
        }

        public override void Visit(InfoMessage message) => Console.WriteLine(message.Text.TrimEnd());
        public override void Visit(AlarmMessage message) => Console.WriteLine($"[ALARM] {message.Text.TrimEnd()}");
        public override void Visit(DiagnoseMessage message) => Tagged(message);
        public override void Visit(LoadInfoMessage message) => Tagged(message);
        public override void Visit(LoadFinishedMessage message) => Tagged(message);
        public override void Visit(LoadErrorMessage message) => Tagged(message);
        public override void Visit(FileQueryReply message) => Tagged(message);
        public override void Visit(UnknownMachineMessage message) => Tagged(message);

        private static void Tagged(IMachineMessage message) =>
            Console.WriteLine($"[rx] {message.Packet}: {message.Text.TrimEnd()}");
    }
}
