using System.Net;
using System.Net.Sockets;

namespace YardTracker.Modbus;

/// <summary>A device (slave) addressed by a unit id.</summary>
public interface IModbusDevice
{
    /// <returns><c>null</c> on success, otherwise the exception code to return to the master.</returns>
    ModbusExceptionCode? ReadHoldingRegisters(ushort startAddress, ushort quantity, Span<ushort> destination);

    ModbusExceptionCode? WriteSingleRegister(ushort address, ushort value);
}

/// <summary>Thread-safe bank of holding registers.</summary>
public class HoldingRegisterDevice(int registerCount) : IModbusDevice
{
    private readonly ushort[] _registers = new ushort[registerCount];
    private readonly Lock _lock = new();

    public ushort this[int address]
    {
        get { lock (_lock) return _registers[address]; }
        set { lock (_lock) _registers[address] = value; }
    }

    public void Write(ushort startAddress, ReadOnlySpan<ushort> values)
    {
        lock (_lock)
            values.CopyTo(_registers.AsSpan(startAddress));
    }

    public ModbusExceptionCode? ReadHoldingRegisters(ushort startAddress, ushort quantity, Span<ushort> destination)
    {
        if (startAddress + quantity > _registers.Length)
            return ModbusExceptionCode.IllegalDataAddress;

        lock (_lock)
            _registers.AsSpan(startAddress, quantity).CopyTo(destination);
        return null;
    }

    public virtual ModbusExceptionCode? WriteSingleRegister(ushort address, ushort value)
    {
        if (address >= _registers.Length)
            return ModbusExceptionCode.IllegalDataAddress;

        this[address] = value;
        return null;
    }
}

/// <summary>
/// Modbus TCP server (slave side). Several unit ids can share one listener, the way a Modbus TCP-to-RTU
/// gateway fronts several serial devices on a production floor.
/// </summary>
public sealed class ModbusTcpServer(IPAddress bindAddress, int port, Func<byte, IModbusDevice?> resolveDevice) : IAsyncDisposable
{
    private readonly TcpListener _listener = new(bindAddress, port);
    private readonly CancellationTokenSource _stopping = new();
    private readonly List<Task> _connections = [];
    private Task? _acceptLoop;

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public event Action<string>? Log;

    public void Start()
    {
        _listener.Start();
        _acceptLoop = AcceptLoopAsync(_stopping.Token);
    }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException) when (token.IsCancellationRequested)
            {
                break;
            }

            lock (_connections)
            {
                _connections.RemoveAll(t => t.IsCompleted);
                _connections.Add(HandleClientAsync(client, token));
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken token)
    {
        var remote = client.Client.RemoteEndPoint?.ToString() ?? "?";
        Log?.Invoke($"Master connected from {remote}");

        using (client)
        {
            client.NoDelay = true;
            var stream = client.GetStream();
            var header = new byte[ModbusFrame.HeaderLength];

            try
            {
                while (!token.IsCancellationRequested)
                {
                    await stream.ReadExactlyAsync(header, token);
                    var mbap = ModbusFrame.ReadHeader(header);

                    if (mbap.ProtocolId != 0 || mbap.PduLength is < 1 or > ModbusFrame.MaxPduLength)
                    {
                        Log?.Invoke($"Invalid MBAP header from {remote}; closing connection");
                        return;
                    }

                    var request = new byte[mbap.PduLength];
                    await stream.ReadExactlyAsync(request, token);

                    var response = Process(resolveDevice(mbap.UnitId), request);
                    await stream.WriteAsync(ModbusFrame.Build(mbap.TransactionId, mbap.UnitId, response), token);
                }
            }
            catch (Exception ex) when (ex is EndOfStreamException or IOException or OperationCanceledException or SocketException)
            {
                // Master disconnected or server stopping.
            }
        }

        Log?.Invoke($"Master {remote} disconnected");
    }

    internal static byte[] Process(IModbusDevice? device, ReadOnlySpan<byte> request)
    {
        var function = request[0];

        if (device == null)
            return ModbusFrame.ExceptionResponse(function, ModbusExceptionCode.GatewayTargetDeviceFailedToRespond);

        switch (function)
        {
            case ModbusFunction.ReadHoldingRegisters when request.Length == 5:
            {
                var start = (ushort)(request[1] << 8 | request[2]);
                var quantity = (ushort)(request[3] << 8 | request[4]);
                if (quantity is 0 or > ModbusFrame.MaxReadQuantity)
                    return ModbusFrame.ExceptionResponse(function, ModbusExceptionCode.IllegalDataValue);

                Span<ushort> values = stackalloc ushort[quantity];
                var error = device.ReadHoldingRegisters(start, quantity, values);
                return error is { } code
                    ? ModbusFrame.ExceptionResponse(function, code)
                    : ModbusFrame.ReadHoldingRegistersResponse(values);
            }

            case ModbusFunction.WriteSingleRegister when request.Length == 5:
            {
                var address = (ushort)(request[1] << 8 | request[2]);
                var value = (ushort)(request[3] << 8 | request[4]);
                var error = device.WriteSingleRegister(address, value);
                return error is { } code
                    ? ModbusFrame.ExceptionResponse(function, code)
                    : request.ToArray();
            }

            case ModbusFunction.ReadHoldingRegisters or ModbusFunction.WriteSingleRegister:
                return ModbusFrame.ExceptionResponse(function, ModbusExceptionCode.IllegalDataValue);

            default:
                return ModbusFrame.ExceptionResponse(function, ModbusExceptionCode.IllegalFunction);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        _listener.Stop();

        if (_acceptLoop != null)
            await _acceptLoop;

        Task[] pending;
        lock (_connections)
            pending = [.. _connections];
        await Task.WhenAll(pending);

        _stopping.Dispose();
    }
}
