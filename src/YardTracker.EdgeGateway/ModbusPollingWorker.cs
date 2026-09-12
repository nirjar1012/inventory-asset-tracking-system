using System.Net.Sockets;
using Microsoft.Extensions.Options;
using YardTracker.Client;
using YardTracker.Contracts;
using YardTracker.Modbus;

namespace YardTracker.EdgeGateway;

public sealed class ModbusOptions
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 502;
    public int PollIntervalSeconds { get; set; } = 5;
    public int TimeoutMilliseconds { get; set; } = 1500;

    /// <summary>Readings kept in memory while the WCF service is unreachable (store-and-forward).</summary>
    public int MaxBufferedReadings { get; set; } = 20000;

    /// <summary>If &gt; 0, write the fault-reset register (FC06) after a machine has been faulted for this many polls.</summary>
    public int AutoResetFaultsAfterPolls { get; set; }
}

/// <summary>
/// Polls every machine's status block (FC03, registers 40001-40005) on a fixed interval, decodes it with the
/// shared register map, and forwards readings in batches. If the service is down, readings are buffered
/// and sent with their original timestamps once it comes back.
/// </summary>
public sealed class ModbusPollingWorker(IOptions<ModbusOptions> options, YardTrackerClient client, ILogger<ModbusPollingWorker> logger)
    : BackgroundService
{
    private readonly ModbusOptions _options = options.Value;
    private readonly List<MachineReading> _buffer = [];
    private readonly Dictionary<string, MachineState> _lastState = [];
    private readonly Dictionary<string, int> _faultPolls = [];
    private bool _plcReachable = true;
    private bool _serviceReachable = true;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var machines = await LoadMachinesAsync(stoppingToken);
        logger.LogInformation("Polling {Count} machines on Modbus TCP {Host}:{Port} every {Interval}s: {Machines}",
            machines.Length, _options.Host, _options.Port, _options.PollIntervalSeconds,
            string.Join(", ", machines.Select(m => $"{m.MachineCode}=unit {m.ModbusUnitId}")));

        using var modbus = new ModbusTcpClient(_options.Host, _options.Port, TimeSpan.FromMilliseconds(_options.TimeoutMilliseconds));
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, _options.PollIntervalSeconds)));

        do
        {
            await PollAsync(modbus, machines, stoppingToken);
            await ForwardAsync();
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task<MachineInfo[]> LoadMachinesAsync(CancellationToken stoppingToken)
    {
        while (true)
        {
            try
            {
                return await client.TelemetryAsync(s => s.GetMachinesAsync());
            }
            catch (Exception ex) when (YardTrackerClient.IsConnectivityFailure(ex))
            {
                logger.LogWarning("Waiting for the YardTracker service to load the machine list: {Message}", ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task PollAsync(ModbusTcpClient modbus, MachineInfo[] machines, CancellationToken stoppingToken)
    {
        var polledAt = DateTime.UtcNow;

        foreach (var machine in machines)
        {
            try
            {
                var registers = await modbus.ReadHoldingRegistersAsync(machine.ModbusUnitId, MachineRegisterMap.Temperature, MachineRegisterMap.StatusBlockLength, stoppingToken);
                var snapshot = MachineRegisterMap.Decode(registers);

                if (!_plcReachable)
                {
                    logger.LogInformation("Modbus connection to {Host}:{Port} restored", _options.Host, _options.Port);
                    _plcReachable = true;
                }

                Record(machine, snapshot, polledAt);
                await AutoResetFaultAsync(modbus, machine, snapshot, stoppingToken);
            }
            catch (ModbusException ex)
            {
                logger.LogWarning("{Machine} (unit {Unit}) returned Modbus exception {Code}", machine.MachineCode, machine.ModbusUnitId, ex.Code);
            }
            catch (Exception ex) when (ex is IOException or SocketException or TimeoutException or InvalidDataException)
            {
                if (_plcReachable)
                    logger.LogError("Modbus connection to {Host}:{Port} failed: {Message}", _options.Host, _options.Port, ex.Message);
                _plcReachable = false;
                return; // the whole connection is down; try again next cycle
            }
        }
    }

    private void Record(MachineInfo machine, MachineSnapshot snapshot, DateTime polledAt)
    {
        if (!_lastState.TryGetValue(machine.MachineCode, out var previous) || previous != snapshot.State)
        {
            var level = snapshot.State == MachineState.Fault ? LogLevel.Warning : LogLevel.Information;
            logger.Log(level, "{Machine} is {State}{Fault} at {Temperature:0.0} C", machine.MachineCode, snapshot.State,
                snapshot.FaultCode != 0 ? $" (fault {snapshot.FaultCode})" : "", snapshot.TemperatureC);
            _lastState[machine.MachineCode] = snapshot.State;
        }

        if (machine.TempAlarmC is decimal alarm && (decimal)snapshot.TemperatureC >= alarm)
            logger.LogWarning("{Machine} temperature {Temperature:0.0} C is at or above the alarm limit {Alarm} C", machine.MachineCode, snapshot.TemperatureC, alarm);

        _buffer.Add(new MachineReading
        {
            MachineCode = machine.MachineCode,
            RecordedAtUtc = polledAt,
            TemperatureC = Math.Round((decimal)snapshot.TemperatureC, 1),
            StatusCode = (short)snapshot.State,
            CycleCount = snapshot.CycleCount,
            FaultCode = (short)snapshot.FaultCode
        });

        if (_buffer.Count > _options.MaxBufferedReadings)
            _buffer.RemoveRange(0, _buffer.Count - _options.MaxBufferedReadings);
    }

    private async Task AutoResetFaultAsync(ModbusTcpClient modbus, MachineInfo machine, MachineSnapshot snapshot, CancellationToken stoppingToken)
    {
        if (_options.AutoResetFaultsAfterPolls <= 0)
            return;

        if (snapshot.State != MachineState.Fault)
        {
            _faultPolls.Remove(machine.MachineCode);
            return;
        }

        var polls = _faultPolls[machine.MachineCode] = _faultPolls.GetValueOrDefault(machine.MachineCode) + 1;
        if (polls >= _options.AutoResetFaultsAfterPolls)
        {
            await modbus.WriteSingleRegisterAsync(machine.ModbusUnitId, MachineRegisterMap.FaultReset, 1, stoppingToken);
            logger.LogWarning("{Machine}: fault {Fault} reset via FC06 after {Polls} polls", machine.MachineCode, snapshot.FaultCode, polls);
            _faultPolls.Remove(machine.MachineCode);
        }
    }

    private async Task ForwardAsync()
    {
        if (_buffer.Count == 0)
            return;

        try
        {
            foreach (var batch in _buffer.Chunk(1000).ToList())
            {
                var stored = await client.TelemetryAsync(s => s.RecordReadingsAsync(batch));
                _buffer.RemoveRange(0, batch.Length);
                logger.LogDebug("Forwarded {Stored}/{Sent} readings", stored, batch.Length);
            }

            if (!_serviceReachable)
            {
                logger.LogInformation("YardTracker service reachable again; buffered readings forwarded");
                _serviceReachable = true;
            }
        }
        catch (Exception ex) when (YardTrackerClient.IsConnectivityFailure(ex))
        {
            if (_serviceReachable)
                logger.LogError("YardTracker service unreachable, buffering readings: {Message}", ex.Message);
            _serviceReachable = false;
            logger.LogDebug("{Count} readings buffered", _buffer.Count);
        }
    }
}
