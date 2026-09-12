using System.Net.Sockets;

namespace YardTracker.Modbus;

/// <summary>
/// Modbus TCP master. One TCP connection, one request at a time (as most PLCs and serial gateways expect).
/// Any I/O error drops the connection; the next call reconnects.
/// </summary>
public sealed class ModbusTcpClient(string host, int port = 502, TimeSpan? timeout = null) : IDisposable
{
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(2);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private ushort _transactionId;

    public string Host { get; } = host;
    public int Port { get; } = port;
    public bool IsConnected => _tcp?.Connected == true;

    public async Task<ushort[]> ReadHoldingRegistersAsync(byte unitId, ushort startAddress, ushort quantity, CancellationToken cancellationToken = default)
    {
        var pdu = await TransactAsync(unitId, ModbusFrame.ReadHoldingRegistersRequest(startAddress, quantity), cancellationToken);
        return ModbusFrame.ParseReadHoldingRegistersResponse(pdu, quantity);
    }

    public async Task WriteSingleRegisterAsync(byte unitId, ushort address, ushort value, CancellationToken cancellationToken = default)
    {
        var pdu = await TransactAsync(unitId, ModbusFrame.WriteSingleRegisterRequest(address, value), cancellationToken);
        ModbusFrame.ParseWriteSingleRegisterResponse(pdu, address, value);
    }

    private async Task<byte[]> TransactAsync(byte unitId, byte[] requestPdu, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(_timeout);
        var token = timeoutSource.Token;

        try
        {
            var stream = await EnsureConnectedAsync(token);
            var transactionId = unchecked(++_transactionId);

            await stream.WriteAsync(ModbusFrame.Build(transactionId, unitId, requestPdu), token);

            var headerBytes = new byte[ModbusFrame.HeaderLength];
            await stream.ReadExactlyAsync(headerBytes, token);
            var header = ModbusFrame.ReadHeader(headerBytes);

            if (header.ProtocolId != 0 || header.PduLength is < 1 or > ModbusFrame.MaxPduLength)
                throw new InvalidDataException("Invalid MBAP header in response.");

            var pdu = new byte[header.PduLength];
            await stream.ReadExactlyAsync(pdu, token);

            if (header.TransactionId != transactionId || header.UnitId != unitId)
                throw new InvalidDataException($"Response for transaction {header.TransactionId}/unit {header.UnitId} does not match request {transactionId}/unit {unitId}.");

            return pdu;
        }
        catch (ModbusException)
        {
            throw;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Disconnect();
            throw new TimeoutException($"Modbus device {Host}:{Port} unit {unitId} did not respond within {_timeout.TotalMilliseconds:N0} ms.");
        }
        catch
        {
            Disconnect();
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<NetworkStream> EnsureConnectedAsync(CancellationToken token)
    {
        if (_stream != null && IsConnected)
            return _stream;

        Disconnect();
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            await tcp.ConnectAsync(Host, Port, token);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }

        _tcp = tcp;
        _stream = tcp.GetStream();
        return _stream;
    }

    private void Disconnect()
    {
        _stream?.Dispose();
        _tcp?.Dispose();
        _stream = null;
        _tcp = null;
    }

    public void Dispose()
    {
        Disconnect();
        _gate.Dispose();
    }
}
