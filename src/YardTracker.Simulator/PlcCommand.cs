using System.Net;
using YardTracker.Modbus;

namespace YardTracker.Simulator;

/// <summary>
/// Stands in for the PLCs on the floor: a Modbus TCP server exposing one unit id per machine.
/// The edge gateway polls it with FC03 exactly as it would a real controller.
/// </summary>
internal static class PlcCommand
{
    public static async Task<int> RunAsync(string bind, int port, double speedup, CancellationToken cancellationToken)
    {
        var devices = MachineProfile.All.ToDictionary(
            p => p.UnitId,
            p => new SimulatedMachineDevice(new MachineModel(p, new Random(p.UnitId * 7919))));

        await using var server = new ModbusTcpServer(IPAddress.Parse(bind), port, unit => devices.GetValueOrDefault(unit));
        server.Log += Out.Info;
        server.Start();

        Out.Ok($"Modbus TCP PLC simulator listening on {bind}:{server.Port}");
        foreach (var device in devices.Values)
            Out.Info($"  unit {device.Model.Profile.UnitId}: {device.Model.Profile.MachineCode}");
        Out.Info("  holding registers: 40001 temp x10 (int16) | 40002 state | 40003-40004 cycles (uint32) | 40005 fault | 40011 write 1 = reset fault");
        Out.Info($"  simulated time runs {speedup:0.#}x real time. Ctrl+C to stop.");

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        var tick = 0;
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                foreach (var device in devices.Values)
                    device.Tick(minutes: speedup / 60.0);

                if (++tick % 5 == 0)
                {
                    Out.Accent(string.Join("  |  ", devices.Values.Select(d =>
                    {
                        var s = d.Model.Snapshot;
                        return $"{d.Model.Profile.MachineCode} {s.State,-11} {s.TemperatureC,6:0.0}C{(s.FaultCode != 0 ? $" F{s.FaultCode}" : "")}";
                    })));
                }
            }
        }
        catch (OperationCanceledException)
        {
        }

        return 0;
    }
}

internal sealed class SimulatedMachineDevice(MachineModel model) : HoldingRegisterDevice(MachineRegisterMap.RegisterCount)
{
    private readonly Lock _modelLock = new();

    public MachineModel Model => model;

    public void Tick(double minutes)
    {
        lock (_modelLock)
        {
            model.Step(minutes, onShift: true, maintenance: false);
            Publish();
        }
    }

    public override ModbusExceptionCode? WriteSingleRegister(ushort address, ushort value)
    {
        // Status registers are read-only; only the fault-reset coil-style register accepts writes.
        if (address != MachineRegisterMap.FaultReset)
            return ModbusExceptionCode.IllegalDataAddress;

        if (value == 1)
        {
            lock (_modelLock)
            {
                model.ClearFault();
                Publish();
            }
            Out.Warn($"{model.Profile.MachineCode}: fault reset by master");
        }
        return null;
    }

    private void Publish()
    {
        Span<ushort> block = stackalloc ushort[MachineRegisterMap.StatusBlockLength];
        MachineRegisterMap.Encode(model.Snapshot, block);
        Write(0, block);
    }
}
