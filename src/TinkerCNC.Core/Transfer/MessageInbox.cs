using System.Threading.Channels;
using TinkerCNC.Protocol;

namespace TinkerCNC.Transfer;

/// <summary>Collects the machine's messages during an exchange, to be read one at a time with a timeout.</summary>
internal sealed class MessageInbox : IDisposable
{
    private readonly MachineConnection _machine;
    private readonly Channel<IMachineMessage> _messages = Channel.CreateUnbounded<IMachineMessage>();

    public MessageInbox(MachineConnection machine)
    {
        _machine = machine;
        machine.MessageReceived += Collect;
    }

    /// <returns>The next message, or null when none arrived within <paramref name="timeout"/>.</returns>
    public async Task<IMachineMessage?> ReadAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try
        {
            return await _messages.Reader.ReadAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    public void Dispose() => _machine.MessageReceived -= Collect;

    private void Collect(IMachineMessage message) => _messages.Writer.TryWrite(message);
}
