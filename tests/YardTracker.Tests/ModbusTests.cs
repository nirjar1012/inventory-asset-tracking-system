using System.Net;
using YardTracker.Modbus;

namespace YardTracker.Tests;

public class ModbusFrameTests
{
    [Fact]
    public void Read_holding_registers_request_matches_the_spec_byte_layout()
    {
        var frame = ModbusFrame.Build(transactionId: 1, unitId: 3, ModbusFrame.ReadHoldingRegistersRequest(startAddress: 0, quantity: 5));

        //                   txn id      protocol    length      unit  fc    start       quantity
        byte[] expected = [0x00, 0x01, 0x00, 0x00, 0x00, 0x06, 0x03, 0x03, 0x00, 0x00, 0x00, 0x05];
        Assert.Equal(expected, frame);
    }

    [Fact]
    public void Header_round_trips()
    {
        var frame = ModbusFrame.Build(0xBEEF, 7, [0x03, 0x02, 0x00, 0x2A]);
        var header = ModbusFrame.ReadHeader(frame);

        Assert.Equal(new MbapHeader(0xBEEF, 0, 5, 7), header);
        Assert.Equal(4, header.PduLength);
    }

    [Fact]
    public void Parses_register_values_big_endian()
    {
        byte[] pdu = [0x03, 0x04, 0x09, 0x0F, 0x00, 0x02];

        Assert.Equal(new ushort[] { 2319, 2 }, ModbusFrame.ParseReadHoldingRegistersResponse(pdu, 2));
    }

    [Fact]
    public void Exception_response_throws_with_the_device_code()
    {
        byte[] pdu = [0x83, 0x02];

        var ex = Assert.Throws<ModbusException>(() => ModbusFrame.ParseReadHoldingRegistersResponse(pdu, 5));
        Assert.Equal(ModbusExceptionCode.IllegalDataAddress, ex.Code);
    }

    [Fact]
    public void Byte_count_that_does_not_match_the_request_is_rejected()
    {
        byte[] pdu = [0x03, 0x02, 0x00, 0x01];

        Assert.Throws<InvalidDataException>(() => ModbusFrame.ParseReadHoldingRegistersResponse(pdu, 5));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(126)]
    public void Read_quantity_must_be_between_1_and_125(ushort quantity) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ModbusFrame.ReadHoldingRegistersRequest(0, quantity));

    [Fact]
    public void Server_answers_unsupported_function_with_illegal_function()
    {
        var response = ModbusTcpServer.Process(new HoldingRegisterDevice(10), [0x2B, 0x0E, 0x01, 0x00]);

        Assert.Equal(new byte[] { 0xAB, (byte)ModbusExceptionCode.IllegalFunction }, response);
    }
}

public class MachineRegisterMapTests
{
    [Fact]
    public void Snapshot_round_trips_through_registers()
    {
        var snapshot = new MachineSnapshot(TemperatureC: 232.4, State: MachineState.Running, CycleCount: 0x0012_D687, FaultCode: 305);
        var registers = new ushort[MachineRegisterMap.StatusBlockLength];

        MachineRegisterMap.Encode(snapshot, registers);

        Assert.Equal(2324, registers[MachineRegisterMap.Temperature]);
        Assert.Equal(0x0012, registers[MachineRegisterMap.CycleCountHigh]);
        Assert.Equal(0xD687, registers[MachineRegisterMap.CycleCountLow]);
        Assert.Equal(snapshot, MachineRegisterMap.Decode(registers));
    }

    [Fact]
    public void Negative_temperatures_use_twos_complement()
    {
        var registers = new ushort[MachineRegisterMap.StatusBlockLength];
        MachineRegisterMap.Encode(new MachineSnapshot(-12.5, MachineState.Idle, 0, 0), registers);

        Assert.Equal(0xFF83, registers[MachineRegisterMap.Temperature]);
        Assert.Equal(-12.5, MachineRegisterMap.Decode(registers).TemperatureC);
    }

    [Fact]
    public void Unknown_state_value_decodes_as_offline()
    {
        ushort[] registers = [250, 99, 0, 0, 0];

        Assert.Equal(MachineState.Offline, MachineRegisterMap.Decode(registers).State);
    }
}

public class ModbusClientServerTests
{
    [Fact]
    public async Task Client_reads_and_writes_registers_over_tcp()
    {
        var device = new HoldingRegisterDevice(16);
        device.Write(0, [2319, 2, 0, 42, 0]);

        await using var server = new ModbusTcpServer(IPAddress.Loopback, 0, unit => unit == 1 ? device : null);
        server.Start();
        using var client = new ModbusTcpClient("127.0.0.1", server.Port, TimeSpan.FromSeconds(2));

        var values = await client.ReadHoldingRegistersAsync(1, 0, 5, TestContext.Current.CancellationToken);
        await client.WriteSingleRegisterAsync(1, 10, 1, TestContext.Current.CancellationToken);

        Assert.Equal(new ushort[] { 2319, 2, 0, 42, 0 }, values);
        Assert.Equal(1, device[10]);
    }

    [Fact]
    public async Task Unknown_unit_id_returns_gateway_exception()
    {
        await using var server = new ModbusTcpServer(IPAddress.Loopback, 0, _ => null);
        server.Start();
        using var client = new ModbusTcpClient("127.0.0.1", server.Port);

        var ex = await Assert.ThrowsAsync<ModbusException>(() => client.ReadHoldingRegistersAsync(9, 0, 5, TestContext.Current.CancellationToken));
        Assert.Equal(ModbusExceptionCode.GatewayTargetDeviceFailedToRespond, ex.Code);
    }

    [Fact]
    public async Task Reading_past_the_register_bank_returns_illegal_data_address_and_keeps_the_connection()
    {
        var device = new HoldingRegisterDevice(8);
        await using var server = new ModbusTcpServer(IPAddress.Loopback, 0, _ => device);
        server.Start();
        using var client = new ModbusTcpClient("127.0.0.1", server.Port);

        var ex = await Assert.ThrowsAsync<ModbusException>(() => client.ReadHoldingRegistersAsync(1, 6, 5, TestContext.Current.CancellationToken));
        Assert.Equal(ModbusExceptionCode.IllegalDataAddress, ex.Code);

        // A Modbus exception is an application-level answer, so the same connection keeps working.
        Assert.Equal(3, (await client.ReadHoldingRegistersAsync(1, 5, 3, TestContext.Current.CancellationToken)).Length);
        Assert.True(client.IsConnected);
    }

    [Fact]
    public async Task Unreachable_device_times_out()
    {
        using var client = new ModbusTcpClient("10.255.255.1", 502, TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<Exception>(() => client.ReadHoldingRegistersAsync(1, 0, 1, TestContext.Current.CancellationToken));
        Assert.False(client.IsConnected);
    }
}
