namespace TinkerCNC.Cli.Commands;

public interface ICliCommand
{
    string Name { get; }
    string Usage { get; }
    Task<int> RunAsync(CliArguments args);
}

/// <summary>Positional arguments after the command name.</summary>
public sealed class CliArguments(string[] values)
{
    public string Required(int index, string name) =>
        index < values.Length ? values[index] : throw new ArgumentException($"missing <{name}>");

    public string? Optional(int index) => index < values.Length ? values[index] : null;
}
