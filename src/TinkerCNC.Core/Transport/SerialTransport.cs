using System.IO.Ports;

namespace TinkerCNC.Transport;

/// <summary>
/// USB-serial (CDC) connection: 115200 baud, 8 data bits, no parity, 1 stop bit, no flow control.
/// </summary>
public sealed class SerialTransport : IMachineTransport
{
    public const int DefaultBaudRate = 115200;

    private SerialPort? _port;

    public SerialTransport(string portName, int baudRate = DefaultBaudRate)
    {
        PortName = portName;
        BaudRate = baudRate;
    }

    public string PortName { get; }
    public int BaudRate { get; }

    public string Description => $"Serial {PortName} @ {BaudRate}";

    public static string[] ListPorts() => SerialPort.GetPortNames();

    public Task<Stream> OpenAsync(CancellationToken cancellationToken = default)
    {
        _port = new SerialPort(PortName, BaudRate, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None,
            ReadTimeout = SerialPort.InfiniteTimeout,
            WriteTimeout = 3000,
        };
        _port.Open();
        return Task.FromResult(_port.BaseStream);
    }

    public ValueTask DisposeAsync()
    {
        if (_port is not null)
        {
            if (_port.IsOpen) _port.Close();
            _port.Dispose();
            _port = null;
        }
        return ValueTask.CompletedTask;
    }
}
