using System.ServiceModel;
using YardTracker.Client;
using YardTracker.Contracts;

namespace YardTracker.Simulator;

/// <summary>Several handhelds scanning through the WCF service, including misreads and wireless retries.</summary>
internal sealed class TrafficCommand(YardTrackerClient client, double ratePerSecond, double retryChance, int seed)
{
    private readonly Random _rng = new(seed ^ Environment.TickCount);

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        Out.Accent($"Simulating handheld traffic at {ratePerSecond:0.##} scans/s against {client.Options.BaseAddress} (Ctrl+C to stop)");
        var delay = TimeSpan.FromSeconds(1 / Math.Max(0.05, ratePerSecond));

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await ScanOnceAsync();
            }
            catch (Exception ex) when (YardTrackerClient.IsConnectivityFailure(ex))
            {
                Out.Error($"Service unreachable ({ex.GetType().Name}); retrying in 5 s");
                await Task.Delay(5000, cancellationToken);
                continue;
            }
            catch (FaultException ex)
            {
                Out.Error("Service fault: " + YardTrackerClient.Describe(ex));
            }

            await Task.Delay(delay * (0.5 + _rng.NextDouble()), cancellationToken);
        }
        return 0;
    }

    private async Task ScanOnceAsync()
    {
        var site = _rng.PickWeighted([("NYD", 60), ("SYD", 25), ("CTP", 15)]);
        var (station, badge) = _rng.Crew(site);
        var roll = _rng.NextDouble();

        if (roll < 0.15 && site != "CTP")
            await ReceiveAsync(site, station, badge);
        else if (roll < 0.50)
            await MoveAsync(site, station, badge);
        else if (roll < 0.70)
            await CheckOutAsync(site, station, badge);
        else if (roll < 0.80)
            await CheckInAsync(site, station, badge);
        else if (roll < 0.95)
            await LookupAsync(site, station);
        else
            await SendScanAsync("CheckOut", NewRequest($"YT-9{_rng.Next(10000, 99999)}", null, station, badge), s => s.CheckOutItemAsync);
    }

    private async Task ReceiveAsync(string site, string station, string badge)
    {
        var product = _rng.PickProduct(site);
        var request = new ReceiveRequest
        {
            ClientScanId = Guid.NewGuid(),
            TagId = _rng.NewTag(_rng.Next(500000, 999999)),
            ProductCode = product.ProductCode,
            HeatNumber = _rng.HeatNumber(),
            Pieces = _rng.Next(product.MinPieces, product.MaxPieces + 1),
            LocationCode = site + "-DOCK1",
            OperatorBadge = badge,
            StationCode = station,
            ScannedAtUtc = DateTime.UtcNow,
            Reference = $"PO-{_rng.Next(40000, 49999)}"
        };
        Print("Receive", station, request.TagId, await client.InventoryAsync(s => s.ReceiveItemAsync(request)));
    }

    private async Task MoveAsync(string site, string station, string badge)
    {
        var items = await ItemsAsync("InYard", site);
        if (items.Length == 0)
            return;

        var item = _rng.Pick(items);
        var targets = Catalog.StorageFor(site, item.Category).Append(site + "-STG").Where(l => l != item.LocationCode).ToArray();
        if (targets.Length == 0)
            return;

        await SendScanAsync("Move", NewRequest(item.TagId, _rng.Pick(targets), station, badge), s => s.MoveItemAsync);
    }

    private async Task CheckOutAsync(string site, string station, string badge)
    {
        var items = (await ItemsAsync("InYard", site))
            .Where(i => Catalog.IsStorage(i.LocationCode) || i.LocationCode?.EndsWith("-STG", StringComparison.Ordinal) == true)
            .ToArray();
        if (items.Length == 0)
            return;

        await SendScanAsync("CheckOut", NewRequest(_rng.Pick(items).TagId, null, station, badge, $"WO-{_rng.Next(10000, 99999)}"), s => s.CheckOutItemAsync);
    }

    private async Task CheckInAsync(string site, string station, string badge)
    {
        var items = await ItemsAsync("CheckedOut", null);
        if (items.Length == 0)
            return;

        var item = _rng.Pick(items);
        var location = _rng.Pick(Catalog.StorageFor(site, item.Category));
        await SendScanAsync("CheckIn", NewRequest(item.TagId, location, station, badge, "RETURN"), s => s.CheckInItemAsync);
    }

    private async Task LookupAsync(string site, string station)
    {
        var items = await ItemsAsync("InYard", site);
        if (items.Length == 0)
            return;

        var tag = _rng.Pick(items).TagId;
        var item = await client.InventoryAsync(s => s.LookupItemAsync(tag));
        Out.Info($"{station,-8} {"Lookup",-16} {tag,-24} {item?.Status} at {item?.LocationCode}, {item?.DaysSinceLastMove} days since last move");
    }

    private async Task SendScanAsync(string action, ScanRequest request, Func<IInventoryService, Func<ScanRequest, Task<ScanResult>>> operation)
    {
        var result = await client.InventoryAsync(s => operation(s)(request));
        Print(action, request.StationCode, request.TagId, result);

        if (result.Outcome == ScanOutcome.Accepted && _rng.NextDouble() < retryChance)
        {
            // The reply was lost on the wireless link, so the handheld resends the identical request.
            var retry = await client.InventoryAsync(s => operation(s)(request));
            Print(action + " (retry)", request.StationCode, request.TagId, retry);
        }
    }

    private Task<ItemInfo[]> ItemsAsync(string status, string? site) =>
        client.InventoryAsync(s => s.SearchItemsAsync(new ItemSearch { Status = status, SiteCode = site, Top = 300 }));

    private static ScanRequest NewRequest(string tag, string? location, string station, string badge, string? reference = null) => new()
    {
        ClientScanId = Guid.NewGuid(),
        TagId = tag,
        LocationCode = location,
        StationCode = station,
        OperatorBadge = badge,
        ScannedAtUtc = DateTime.UtcNow,
        Reference = reference
    };

    private static void Print(string action, string station, string tag, ScanResult result)
    {
        var color = result.Outcome switch
        {
            ScanOutcome.Accepted => ConsoleColor.Green,
            ScanOutcome.Duplicate => ConsoleColor.Cyan,
            _ => ConsoleColor.Yellow
        };
        var type = result.ExceptionType == null ? "" : $" [{result.ExceptionType}]";
        Out.Write(color, $"{station,-8} {action,-16} {tag,-24} {result.Outcome}{type}: {result.Message}");
    }
}
