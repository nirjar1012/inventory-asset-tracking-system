using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using YardTracker.Client;
using YardTracker.Contracts;
using YardTracker.Station.ViewModels;

namespace YardTracker.Station.Services;

public sealed class StationSettings
{
    public string StationCode { get; set; } = "HH-01";
    public ServiceEndpointOptions Service { get; set; } = new();
    public int OfflineRetrySeconds { get; set; } = 10;

    public static StationSettings Load(string[] args)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "station.json");
        var settings = File.Exists(path)
            ? JsonSerializer.Deserialize<StationSettings>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new()
            : new StationSettings();

        for (var i = 0; i + 1 < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--station": settings.StationCode = args[++i].ToUpperInvariant(); break;
                case "--service": settings.Service.BaseAddress = args[++i]; break;
                case "--security": settings.Service.Security = args[++i]; break;
            }
        }
        return settings;
    }
}

public enum QueuedScanKind
{
    CheckIn,
    CheckOut,
    Move,
    Receive
}

public sealed class QueuedScan
{
    public QueuedScanKind Kind { get; set; }
    public ScanRequest? Scan { get; set; }
    public ReceiveRequest? Receive { get; set; }
    public DateTime QueuedAtUtc { get; set; } = DateTime.UtcNow;

    [JsonIgnore]
    public Guid ClientScanId => Scan?.ClientScanId ?? Receive?.ClientScanId ?? Guid.Empty;
}

/// <summary>
/// Scans taken while the handheld is out of Wi-Fi range. Persisted to disk after every change so a dead
/// battery does not lose them. Each scan keeps its ClientScanId and device timestamp, so replaying it is
/// idempotent and the server records when the tag was actually read.
/// </summary>
public sealed class OfflineScanQueue
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    private readonly string _path;
    private readonly List<QueuedScan> _items;
    private readonly Lock _lock = new();

    public OfflineScanQueue(string stationCode)
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YardTracker");
        Directory.CreateDirectory(folder);
        _path = Path.Combine(folder, $"offline-queue-{stationCode}.json");
        _items = File.Exists(_path)
            ? JsonSerializer.Deserialize<List<QueuedScan>>(File.ReadAllText(_path), JsonOptions) ?? []
            : [];
    }

    public event Action? Changed;

    public int Count
    {
        get { lock (_lock) return _items.Count; }
    }

    public IReadOnlyList<QueuedScan> Snapshot()
    {
        lock (_lock) return [.. _items];
    }

    public void Enqueue(QueuedScan scan)
    {
        lock (_lock)
        {
            _items.Add(scan);
            Save();
        }
        Changed?.Invoke();
    }

    public void Remove(QueuedScan scan)
    {
        lock (_lock)
        {
            _items.RemoveAll(i => i.ClientScanId == scan.ClientScanId);
            Save();
        }
        Changed?.Invoke();
    }

    private void Save()
    {
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_items, JsonOptions));
        File.Move(temp, _path, overwrite: true);
    }
}

public sealed record SubmitResult(ScanResult? Result)
{
    public bool Queued => Result == null;
}

/// <summary>Sends scans to the service, falling back to the offline queue when the network is down.</summary>
public sealed class ScanSubmitter(YardTrackerClient client, OfflineScanQueue queue)
{
    private readonly SemaphoreSlim _flushGate = new(1, 1);

    public event Action<QueuedScan, ScanResult>? Synced;
    public event Action<bool>? ConnectivityChanged;

    public bool IsOnline { get; private set; } = true;

    public async Task<SubmitResult> SubmitAsync(QueuedScan scan)
    {
        // Keep scans in order: while older scans are still queued, new ones wait behind them.
        if (queue.Count > 0)
        {
            queue.Enqueue(scan);
            _ = FlushAsync();
            return new SubmitResult(null);
        }

        try
        {
            var result = await SendAsync(scan);
            SetOnline(true);
            return new SubmitResult(result);
        }
        catch (Exception ex) when (YardTrackerClient.IsConnectivityFailure(ex))
        {
            queue.Enqueue(scan);
            SetOnline(false);
            return new SubmitResult(null);
        }
    }

    public async Task FlushAsync()
    {
        if (!await _flushGate.WaitAsync(0))
            return;

        try
        {
            foreach (var scan in queue.Snapshot())
            {
                ScanResult result;
                try
                {
                    result = await SendAsync(scan);
                    SetOnline(true);
                }
                catch (Exception ex) when (YardTrackerClient.IsConnectivityFailure(ex))
                {
                    SetOnline(false);
                    return;
                }
                catch (Exception ex)
                {
                    // The service rejected the request itself (validation fault). Retrying will not help.
                    result = new ScanResult { Outcome = ScanOutcome.Rejected, Message = YardTrackerClient.Describe(ex) };
                }

                queue.Remove(scan);
                Synced?.Invoke(scan, result);
            }
        }
        finally
        {
            _flushGate.Release();
        }
    }

    /// <summary>Cheap round trip used to detect that the network is back when nothing is queued.</summary>
    public async Task ProbeAsync()
    {
        try
        {
            await client.LogisticsAsync(s => s.GetSitesAsync());
            SetOnline(true);
        }
        catch (Exception ex) when (YardTrackerClient.IsConnectivityFailure(ex))
        {
            SetOnline(false);
        }
    }

    private Task<ScanResult> SendAsync(QueuedScan scan) => scan.Kind switch
    {
        QueuedScanKind.CheckIn => client.InventoryAsync(s => s.CheckInItemAsync(scan.Scan!)),
        QueuedScanKind.CheckOut => client.InventoryAsync(s => s.CheckOutItemAsync(scan.Scan!)),
        QueuedScanKind.Move => client.InventoryAsync(s => s.MoveItemAsync(scan.Scan!)),
        _ => client.InventoryAsync(s => s.ReceiveItemAsync(scan.Receive!))
    };

    private void SetOnline(bool online)
    {
        if (IsOnline == online)
            return;
        IsOnline = online;
        ConnectivityChanged?.Invoke(online);
    }
}

/// <summary>
/// Stand-in for an RFID reader: "reads" a tag that makes sense for the current mode, so the whole flow
/// can be demonstrated without hardware. A real reader would raise the same tag string.
/// </summary>
public sealed class SimulatedRfidReader(YardTrackerClient client)
{
    private readonly Random _rng = new();

    public async Task<string?> ReadAsync(ScanMode mode, string? siteCode)
    {
        if (mode == ScanMode.Receive)
            return "E28011606000" + _rng.NextInt64(0, 0xFFFFFFFFFFFF).ToString("X12");

        var search = mode == ScanMode.CheckIn
            ? new ItemSearch { Status = "CheckedOut", Top = 200 }
            : new ItemSearch { Status = "InYard", SiteCode = siteCode, Top = 200 };

        var items = await client.InventoryAsync(s => s.SearchItemsAsync(search));
        return items.Length == 0 ? null : items[_rng.Next(items.Length)].TagId;
    }
}
