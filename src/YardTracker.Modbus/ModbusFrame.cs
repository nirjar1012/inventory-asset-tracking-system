using System.Buffers.Binary;

namespace YardTracker.Modbus;

public static class ModbusFunction
{
    public const byte ReadHoldingRegisters = 0x03;
    public const byte WriteSingleRegister = 0x06;
    public const byte ExceptionFlag = 0x80;
}

public enum ModbusExceptionCode : byte
{
    IllegalFunction = 0x01,
    IllegalDataAddress = 0x02,
    IllegalDataValue = 0x03,
    ServerDeviceFailure = 0x04,
    GatewayPathUnavailable = 0x0A,
    GatewayTargetDeviceFailedToRespond = 0x0B
}

public sealed class ModbusException(byte functionCode, ModbusExceptionCode code)
    : Exception($"Modbus exception {(byte)code:X2} ({code}) for function 0x{functionCode:X2}.")
{
    public byte FunctionCode { get; } = functionCode;
    public ModbusExceptionCode Code { get; } = code;
}

/// <summary>Modbus Application Protocol header that prefixes every Modbus TCP frame.</summary>
public readonly record struct MbapHeader(ushort TransactionId, ushort ProtocolId, ushort Length, byte UnitId)
{
    /// <summary>Number of PDU bytes that follow the header (Length counts the unit id too).</summary>
    public int PduLength => Length - 1;
}

/// <summary>
/// Encoding and decoding of Modbus TCP frames.
/// <code>
/// | Transaction id (2) | Protocol id = 0 (2) | Length (2) | Unit id (1) | Function (1) | Data (n) |
/// |&lt;------------------------- MBAP header, 7 bytes --------------------&gt;|&lt;----- PDU ---------&gt;|
/// </code>
/// All multi-byte fields are big-endian.
/// </summary>
public static class ModbusFrame
{
    public const int HeaderLength = 7;
    public const int MaxPduLength = 253;
    public const ushort MaxReadQuantity = 125;

    public static byte[] Build(ushort transactionId, byte unitId, ReadOnlySpan<byte> pdu)
    {
        if (pdu.Length is 0 or > MaxPduLength)
            throw new ArgumentOutOfRangeException(nameof(pdu), "PDU must be 1-253 bytes.");

        var frame = new byte[HeaderLength + pdu.Length];
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(0), transactionId);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2), 0);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4), (ushort)(pdu.Length + 1));
        frame[6] = unitId;
        pdu.CopyTo(frame.AsSpan(HeaderLength));
        return frame;
    }

    public static MbapHeader ReadHeader(ReadOnlySpan<byte> header)
    {
        if (header.Length < HeaderLength)
            throw new ArgumentException("MBAP header is 7 bytes.", nameof(header));

        return new MbapHeader(
            BinaryPrimitives.ReadUInt16BigEndian(header),
            BinaryPrimitives.ReadUInt16BigEndian(header[2..]),
            BinaryPrimitives.ReadUInt16BigEndian(header[4..]),
            header[6]);
    }

    public static byte[] ReadHoldingRegistersRequest(ushort startAddress, ushort quantity)
    {
        if (quantity is 0 or > MaxReadQuantity)
            throw new ArgumentOutOfRangeException(nameof(quantity), "Quantity must be 1-125.");

        var pdu = new byte[5];
        pdu[0] = ModbusFunction.ReadHoldingRegisters;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1), startAddress);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3), quantity);
        return pdu;
    }

    public static byte[] WriteSingleRegisterRequest(ushort address, ushort value)
    {
        var pdu = new byte[5];
        pdu[0] = ModbusFunction.WriteSingleRegister;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1), address);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3), value);
        return pdu;
    }

    public static byte[] ReadHoldingRegistersResponse(ReadOnlySpan<ushort> values)
    {
        var pdu = new byte[2 + values.Length * 2];
        pdu[0] = ModbusFunction.ReadHoldingRegisters;
        pdu[1] = (byte)(values.Length * 2);
        for (var i = 0; i < values.Length; i++)
            BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(2 + i * 2), values[i]);
        return pdu;
    }

    public static byte[] ExceptionResponse(byte functionCode, ModbusExceptionCode code) =>
        [(byte)(functionCode | ModbusFunction.ExceptionFlag), (byte)code];

    public static ushort[] ParseReadHoldingRegistersResponse(ReadOnlySpan<byte> pdu, ushort expectedQuantity)
    {
        ThrowIfException(pdu, ModbusFunction.ReadHoldingRegisters);

        if (pdu.Length < 2 || pdu[1] != expectedQuantity * 2 || pdu.Length != 2 + expectedQuantity * 2)
            throw new InvalidDataException("Malformed read holding registers response.");

        var values = new ushort[expectedQuantity];
        for (var i = 0; i < values.Length; i++)
            values[i] = BinaryPrimitives.ReadUInt16BigEndian(pdu[(2 + i * 2)..]);
        return values;
    }

    public static void ParseWriteSingleRegisterResponse(ReadOnlySpan<byte> pdu, ushort address, ushort value)
    {
        ThrowIfException(pdu, ModbusFunction.WriteSingleRegister);

        // A successful FC06 response echoes the request.
        if (pdu.Length != 5
            || BinaryPrimitives.ReadUInt16BigEndian(pdu[1..]) != address
            || BinaryPrimitives.ReadUInt16BigEndian(pdu[3..]) != value)
            throw new InvalidDataException("Malformed write single register response.");
    }

    private static void ThrowIfException(ReadOnlySpan<byte> pdu, byte functionCode)
    {
        if (pdu.Length == 0)
            throw new InvalidDataException("Empty PDU.");

        if (pdu[0] == (functionCode | ModbusFunction.ExceptionFlag))
            throw new ModbusException(functionCode, pdu.Length > 1 ? (ModbusExceptionCode)pdu[1] : ModbusExceptionCode.ServerDeviceFailure);

        if (pdu[0] != functionCode)
            throw new InvalidDataException($"Unexpected function code 0x{pdu[0]:X2} in response.");
    }
}
