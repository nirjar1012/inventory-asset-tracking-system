using YardTracker.Modbus;

namespace YardTracker.Simulator;

internal sealed record MachineProfile(
    string MachineCode,
    byte UnitId,
    double RunTempC,
    double IdleTempC,
    double Utilization,
    double CyclesPerHour,
    double MeanHoursBetweenFaults,
    ushort[] FaultCodes,
    double OvershootChance)
{
    /// <summary>Matches telemetry.Machines in database/04_seed.sql.</summary>
    public static readonly MachineProfile[] All =
    [
        new("CRN-N1", 1, RunTempC: 64, IdleTempC: 36, Utilization: 0.55, CyclesPerHour: 14, MeanHoursBetweenFaults: 120, FaultCodes: [110, 115], OvershootChance: 0.00),
        new("SAW-C1", 2, RunTempC: 49, IdleTempC: 31, Utilization: 0.60, CyclesPerHour: 30, MeanHoursBetweenFaults: 90, FaultCodes: [204, 207], OvershootChance: 0.00),
        new("OVN-C1", 3, RunTempC: 232, IdleTempC: 150, Utilization: 0.85, CyclesPerHour: 3, MeanHoursBetweenFaults: 160, FaultCodes: [301, 305], OvershootChance: 0.05),
        // The threader runs flat out and faults often: the plant's bottleneck.
        new("THR-C1", 4, RunTempC: 55, IdleTempC: 32, Utilization: 0.90, CyclesPerHour: 10, MeanHoursBetweenFaults: 40, FaultCodes: [402, 410], OvershootChance: 0.00)
    ];
}

/// <summary>
/// Simple behavioural model of a machine controller: a state machine (offline, idle, running, fault,
/// maintenance) plus a first-order temperature response. Used both for the live Modbus PLC simulator
/// and for backfilling historical telemetry.
/// </summary>
internal sealed class MachineModel(MachineProfile profile, Random rng)
{
    private const double AmbientC = 28;
    private double _cycleRemainder;

    public MachineProfile Profile => profile;
    public MachineState State { get; private set; } = MachineState.Offline;
    public double TemperatureC { get; private set; } = AmbientC;
    public uint CycleCount { get; private set; } = (uint)rng.Next(10_000, 90_000);
    public ushort FaultCode { get; private set; }

    public MachineSnapshot Snapshot => new(Math.Round(TemperatureC, 1), State, CycleCount, FaultCode);

    public void Step(double minutes, bool onShift, bool maintenance)
    {
        if (State == MachineState.Fault)
        {
            if (Chance(minutes, meanMinutes: 40))
                ClearFault();
        }
        else if (maintenance)
        {
            State = MachineState.Maintenance;
        }
        else if (!onShift)
        {
            State = MachineState.Offline;
        }
        else if (State == MachineState.Running && Chance(minutes, profile.MeanHoursBetweenFaults * 60))
        {
            State = MachineState.Fault;
            FaultCode = profile.FaultCodes[rng.Next(profile.FaultCodes.Length)];
        }
        else if (State is MachineState.Offline or MachineState.Maintenance || Chance(minutes, meanMinutes: 25))
        {
            State = rng.NextDouble() < profile.Utilization ? MachineState.Running : MachineState.Idle;
        }

        var target = State switch
        {
            MachineState.Running => profile.RunTempC + (rng.NextDouble() < profile.OvershootChance ? 22 : 0),
            MachineState.Idle or MachineState.Fault => profile.IdleTempC,
            _ => AmbientC
        };
        var response = 1 - Math.Exp(-minutes / 18.0);
        var noise = (rng.NextDouble() - 0.5) * Math.Min(2.0, 0.4 + minutes * 0.05);
        TemperatureC += (target - TemperatureC) * response + noise;

        if (State == MachineState.Running)
        {
            _cycleRemainder += profile.CyclesPerHour * minutes / 60 * (0.7 + rng.NextDouble() * 0.6);
            var whole = (uint)_cycleRemainder;
            CycleCount += whole;
            _cycleRemainder -= whole;
        }
    }

    public void ClearFault()
    {
        if (State == MachineState.Fault)
            State = MachineState.Idle;
        FaultCode = 0;
    }

    private bool Chance(double minutes, double meanMinutes) => rng.NextDouble() < 1 - Math.Exp(-minutes / meanMinutes);
}
