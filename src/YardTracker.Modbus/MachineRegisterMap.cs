namespace YardTracker.Modbus;

public enum MachineState : ushort
{
    Offline = 0,
    Idle = 1,
    Running = 2,
    Fault = 3,
    Maintenance = 4
}

public readonly record struct MachineSnapshot(double TemperatureC, MachineState State, uint CycleCount, ushort FaultCode);

/// <summary>
/// Holding-register layout exposed by every yard machine controller. Addresses are zero-based
/// on the wire; the 4xxxx numbers are the conventional one-based PLC register numbers.
/// See docs/modbus-register-map.md.
/// </summary>
public static class MachineRegisterMap
{
    /// <summary>40001: temperature, signed 16-bit, tenths of a degree C.</summary>
    public const ushort Temperature = 0;

    /// <summary>40002: <see cref="MachineState"/>.</summary>
    public const ushort State = 1;

    /// <summary>40003: cycle counter, unsigned 32-bit, high word.</summary>
    public const ushort CycleCountHigh = 2;

    /// <summary>40004: cycle counter, low word.</summary>
    public const ushort CycleCountLow = 3;

    /// <summary>40005: vendor fault code, 0 = no fault.</summary>
    public const ushort FaultCode = 4;

    /// <summary>40011: write 1 to acknowledge and clear a fault (FC06).</summary>
    public const ushort FaultReset = 10;

    /// <summary>Registers 40001-40005 are read in one FC03 request.</summary>
    public const ushort StatusBlockLength = 5;

    public const int RegisterCount = 16;

    public static void Encode(MachineSnapshot snapshot, Span<ushort> registers)
    {
        var tenths = Math.Clamp(Math.Round(snapshot.TemperatureC * 10), short.MinValue, short.MaxValue);
        registers[Temperature] = unchecked((ushort)(short)tenths);
        registers[State] = (ushort)snapshot.State;
        registers[CycleCountHigh] = (ushort)(snapshot.CycleCount >> 16);
        registers[CycleCountLow] = (ushort)(snapshot.CycleCount & 0xFFFF);
        registers[FaultCode] = snapshot.FaultCode;
    }

    public static MachineSnapshot Decode(ReadOnlySpan<ushort> registers)
    {
        if (registers.Length < StatusBlockLength)
            throw new ArgumentException($"Need {StatusBlockLength} registers.", nameof(registers));

        return new MachineSnapshot(
            TemperatureC: unchecked((short)registers[Temperature]) / 10.0,
            State: Enum.IsDefined((MachineState)registers[State]) ? (MachineState)registers[State] : MachineState.Offline,
            CycleCount: (uint)registers[CycleCountHigh] << 16 | registers[CycleCountLow],
            FaultCode: registers[FaultCode]);
    }
}
