using System.ServiceModel;
using YardTracker.Client;
using YardTracker.Contracts;

namespace YardTracker.Simulator;

/// <summary>
/// Driver handhelds on trucks moving stock between sites: load (StartDelivery), drive while reporting GPS
/// positions, unload (CompleteDelivery). Trips are time-compressed; reported speeds are realistic.
/// </summary>
internal sealed class TruckCommand(YardTrackerClient client, int truckCount, int tripSeconds, double pingSeconds, int seed)
{
    private static readonly (string From, string To)[] Routes = [("NYD", "CTP"), ("CTP", "SYD"), ("SYD", "NYD"), ("CTP", "NYD")];

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var sites = await WaitForServiceAsync(cancellationToken);
        var trucks = Catalog.Trucks.Take(Math.Clamp(truckCount, 1, Catalog.Trucks.Length)).ToArray();

        Out.Accent($"Simulating {trucks.Length} truck(s), ~{tripSeconds}s per trip, GPS ping every {pingSeconds:0.#}s (Ctrl+C to stop)");
        await Task.WhenAll(trucks.Select((truck, index) =>
            RunTruckAsync(truck.Code, truck.PayloadLbs, index, sites, new Random(seed + index * 101), cancellationToken)));
        return 0;
    }

    private async Task<Dictionary<string, SiteInfo>> WaitForServiceAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            try
            {
                return (await client.LogisticsAsync(s => s.GetSitesAsync())).ToDictionary(s => s.SiteCode);
            }
            catch (Exception ex) when (YardTrackerClient.IsConnectivityFailure(ex))
            {
                Out.Error("Waiting for the YardTracker service: " + ex.Message);
                await Task.Delay(5000, cancellationToken);
            }
        }
    }

    private async Task RunTruckAsync(string truck, decimal payloadLbs, int index, Dictionary<string, SiteInfo> sites, Random rng, CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(index * 7), cancellationToken);
        var trip = index;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var status = (await client.LogisticsAsync(s => s.GetFleetStatusAsync())).FirstOrDefault(t => t.TruckCode == truck);

                if (status?.DeliveryId is int activeDelivery && status.FromSiteCode != null && status.ToSiteCode != null)
                {
                    Out.Info($"{truck}: resuming delivery #{activeDelivery} {status.FromSiteCode} -> {status.ToSiteCode}");
                    await DriveAsync(truck, activeDelivery, sites[status.FromSiteCode], sites[status.ToSiteCode], rng, cancellationToken, startFraction: 0.5);
                }
                else
                {
                    var (from, to) = Routes[trip++ % Routes.Length];
                    var deliveryId = await LoadAsync(truck, payloadLbs, from, to, rng);
                    if (deliveryId == null)
                    {
                        await Task.Delay(3000, cancellationToken);
                        continue;
                    }
                    await DriveAsync(truck, deliveryId.Value, sites[from], sites[to], rng, cancellationToken);
                }

                await Task.Delay(TimeSpan.FromSeconds(5 + rng.Next(10)), cancellationToken);
            }
            catch (Exception ex) when (YardTrackerClient.IsConnectivityFailure(ex) || ex is FaultException)
            {
                Out.Error($"{truck}: {YardTrackerClient.Describe(ex)}; retrying in 5 s");
                await Task.Delay(5000, cancellationToken);
            }
        }
    }

    private async Task<int?> LoadAsync(string truck, decimal payloadLbs, string from, string to, Random rng)
    {
        var items = await client.InventoryAsync(s => s.SearchItemsAsync(new ItemSearch { Status = "InYard", SiteCode = from, Top = 500 }));
        var candidates = items
            .Where(i => Catalog.IsStorage(i.LocationCode) || i.LocationCode?.EndsWith("-STG", StringComparison.Ordinal) == true)
            .OrderBy(_ => rng.Next());

        var load = new List<ItemInfo>();
        var weight = 0m;
        foreach (var item in candidates)
        {
            if (load.Count == 5)
                break;
            if (weight + item.WeightLbs > Math.Min(payloadLbs, 44000))
                continue;
            load.Add(item);
            weight += item.WeightLbs;
        }

        if (load.Count == 0)
        {
            Out.Warn($"{truck}: nothing to haul from {from}");
            return null;
        }

        var request = new StartDeliveryRequest
        {
            ClientRequestId = Guid.NewGuid(),
            TruckCode = truck,
            DriverBadge = rng.Pick(Catalog.DriverBadges),
            FromSiteCode = from,
            ToSiteCode = to,
            TagIds = load.Select(i => i.TagId).ToArray(),
            StationCode = Catalog.DriverStation
        };

        var result = await client.LogisticsAsync(s => s.StartDeliveryAsync(request));
        if (result.Outcome != ScanOutcome.Accepted || result.DeliveryId == null)
        {
            Out.Warn($"{truck}: {result.Message}");
            return null;
        }

        Out.Ok($"{truck}: delivery #{result.DeliveryId} departed {from} -> {to} with {result.ItemCount} items ({result.WeightLbs / 2000:0.0} t)");
        return result.DeliveryId;
    }

    private async Task DriveAsync(string truck, int deliveryId, SiteInfo from, SiteInfo to, Random rng, CancellationToken cancellationToken, double startFraction = 0)
    {
        var steps = Math.Max(5, (int)(tripSeconds / pingSeconds));
        var bend = (rng.NextDouble() - 0.5) * 0.3;
        var simulatedHours = Geo.Miles(from.Latitude, from.Longitude, to.Latitude, to.Longitude) * 1.35 / 32;
        var firstStep = (int)(startFraction * steps);
        var previous = Geo.Along(from.Latitude, from.Longitude, to.Latitude, to.Longitude, (double)firstStep / steps, bend);

        for (var step = firstStep; step <= steps; step++)
        {
            var point = Geo.Along(from.Latitude, from.Longitude, to.Latitude, to.Longitude, (double)step / steps, bend);
            var segmentMiles = Geo.Miles(previous.Lat, previous.Lon, point.Lat, point.Lon);
            var speed = step == steps ? 0 : Math.Min(65, segmentMiles / (simulatedHours / steps) * (0.85 + rng.NextDouble() * 0.3));

            var position = new GpsPosition
            {
                TruckCode = truck,
                Latitude = point.Lat,
                Longitude = point.Lon,
                SpeedMph = Math.Round(speed, 1),
                HeadingDeg = Geo.Bearing(previous.Lat, previous.Lon, point.Lat, point.Lon),
                RecordedAtUtc = DateTime.UtcNow
            };
            await client.LogisticsAsync(s => s.ReportPositionAsync(position));

            if (step % 10 == 0)
                Out.Info($"{truck}: {point.Lat:F5}, {point.Lon:F5}  {speed,3:0} mph  {100.0 * step / steps,3:0}% to {to.SiteCode}");

            previous = point;
            if (step < steps)
                await Task.Delay(TimeSpan.FromSeconds(pingSeconds), cancellationToken);
        }

        var (station, badge) = rng.Crew(to.SiteCode);
        var request = new CompleteDeliveryRequest
        {
            DeliveryId = deliveryId,
            ArrivalLocationCode = to.SiteCode + "-DOCK1",
            OperatorBadge = badge,
            StationCode = station
        };
        var result = await client.LogisticsAsync(s => s.CompleteDeliveryAsync(request));
        Out.Write(result.Outcome == ScanOutcome.Rejected ? ConsoleColor.Yellow : ConsoleColor.Green, $"{truck}: {result.Message}");
    }
}
