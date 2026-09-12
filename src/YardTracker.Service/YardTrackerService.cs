using System;
using System.Data;
using System.Linq;
using System.ServiceModel;
using System.Threading.Tasks;
using YardTracker.Contracts;
using YardTracker.Service.Data;

namespace YardTracker.Service
{
    /// <summary>
    /// One service class implementing the three contracts. Per-call instancing keeps it stateless,
    /// so it scales with the number of handhelds and survives a client dropping mid-call.
    /// Business rules live in stored procedures; this layer validates input, maps data and logs.
    /// </summary>
    [ServiceBehavior(
        InstanceContextMode = InstanceContextMode.PerCall,
        ConcurrencyMode = ConcurrencyMode.Multiple,
        Namespace = ContractNamespaces.Service)]
    public sealed class YardTrackerService : IInventoryService, ILogisticsService, ITelemetryService
    {
        private static readonly string[] ItemStatuses = { "InYard", "CheckedOut", "InTransit" };

        private static Db Db => Db.Default;

        // ================================================================ inventory

        public Task<StationSession> SignInAsync(string badgeNumber, string stationCode) =>
            Operation.Run(nameof(SignInAsync), $"{stationCode} badge {badgeNumber}", async () =>
            {
                var badge = Badge(badgeNumber);
                var station = Code(stationCode, nameof(stationCode));

                var session = await Db.QuerySingleAsync("dbo.usp_SignIn", p =>
                {
                    p.AddVarChar("@BadgeNumber", badge, 20);
                    p.AddVarChar("@StationCode", station, 20);
                }, Mapping.ToSession).ConfigureAwait(false);

                return session ?? throw new ServiceValidationException("Sign-in failed.");
            }, s => $"{s.OperatorName} ({s.Role})");

        public Task<LocationInfo[]> GetLocationsAsync(string? siteCode) =>
            Operation.Run(nameof(GetLocationsAsync), siteCode ?? "all sites", async () =>
            {
                var rows = await Db.QueryAsync("dbo.usp_GetLocations",
                    p => p.AddVarChar("@SiteCode", OptionalCode(siteCode, nameof(siteCode), 10), 10),
                    Mapping.ToLocation).ConfigureAwait(false);
                return rows.ToArray();
            }, r => $"{r.Length} locations");

        public Task<ProductInfo[]> GetProductsAsync() =>
            Operation.Run(nameof(GetProductsAsync), string.Empty, async () =>
                (await Db.QueryAsync("dbo.usp_GetProducts", null, Mapping.ToProduct).ConfigureAwait(false)).ToArray(),
                r => $"{r.Length} products");

        public Task<ItemInfo?> LookupItemAsync(string tagId) =>
            Operation.Run(nameof(LookupItemAsync), tagId, async () =>
                await Db.QuerySingleAsync("dbo.usp_GetItemByTag",
                    p => p.AddVarChar("@TagId", Tag(tagId), 64),
                    Mapping.ToItem).ConfigureAwait(false),
                item => item == null ? "not found" : $"{item.Status} {item.LocationCode}");

        public Task<ItemInfo[]> SearchItemsAsync(ItemSearch search) =>
            Operation.Run(nameof(SearchItemsAsync), $"{search?.Status} {search?.LocationCode} {search?.SiteCode}", async () =>
            {
                Guard.NotNull(search, nameof(search));
                if (search!.Status != null && !ItemStatuses.Contains(search.Status))
                    throw new ServiceValidationException("Status must be InYard, CheckedOut or InTransit.");

                var rows = await Db.QueryAsync("dbo.usp_SearchItems", p =>
                {
                    p.AddVarChar("@Status", search.Status, 12);
                    p.AddVarChar("@LocationCode", OptionalCode(search.LocationCode, "LocationCode", 20), 20);
                    p.AddVarChar("@SiteCode", OptionalCode(search.SiteCode, "SiteCode", 10), 10);
                    p.AddInt("@Top", Guard.Clamp(search.Top, 1, 1000));
                }, Mapping.ToItem).ConfigureAwait(false);
                return rows.ToArray();
            }, r => $"{r.Length} items");

        public Task<ScanResult> ReceiveItemAsync(ReceiveRequest request) =>
            Operation.Run(nameof(ReceiveItemAsync), Describe(request?.StationCode, request?.TagId), async () =>
            {
                Guard.NotNull(request, nameof(request));
                var r = request!;

                if (r.Pieces <= 0)
                    throw new ServiceValidationException("Pieces must be greater than zero.");

                return await Db.QuerySingleAsync("dbo.usp_ReceiveItem", p =>
                {
                    p.AddVarChar("@TagId", Tag(r.TagId), 64);
                    p.AddVarChar("@ProductCode", Guard.Required(r.ProductCode, "ProductCode", 30).ToUpperInvariant(), 30);
                    p.AddVarChar("@HeatNumber", Guard.Required(r.HeatNumber, "HeatNumber", 20), 20);
                    p.AddInt("@Pieces", r.Pieces);
                    p.AddDecimal("@TotalLengthFt", r.TotalLengthFt, 9, 2);
                    p.AddVarChar("@LocationCode", Code(r.LocationCode, "LocationCode"), 20);
                    p.AddVarChar("@OperatorBadge", Badge(r.OperatorBadge), 20);
                    p.AddVarChar("@StationCode", Code(r.StationCode, "StationCode"), 20);
                    p.AddGuid("@ClientScanId", Guard.NotEmpty(r.ClientScanId, "ClientScanId"));
                    p.AddDateTime2("@ScannedAtUtc", Guard.Utc(r.ScannedAtUtc));
                    p.AddNVarChar("@Reference", Guard.Optional(r.Reference, "Reference", 50), 50);
                }, Mapping.ToScanResult).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("usp_ReceiveItem returned no result.");
            }, DescribeScan);

        public Task<ScanResult> CheckInItemAsync(ScanRequest request) =>
            RecordScan(nameof(CheckInItemAsync), "dbo.usp_CheckInItem", request, withLocation: true);

        public Task<ScanResult> CheckOutItemAsync(ScanRequest request) =>
            RecordScan(nameof(CheckOutItemAsync), "dbo.usp_CheckOutItem", request, withLocation: false);

        public Task<ScanResult> MoveItemAsync(ScanRequest request) =>
            RecordScan(nameof(MoveItemAsync), "dbo.usp_MoveItem", request, withLocation: true);

        private static Task<ScanResult> RecordScan(string operation, string procedure, ScanRequest request, bool withLocation) =>
            Operation.Run(operation, Describe(request?.StationCode, request?.TagId), async () =>
            {
                Guard.NotNull(request, nameof(request));
                var r = request!;

                return await Db.QuerySingleAsync(procedure, p =>
                {
                    p.AddVarChar("@TagId", Tag(r.TagId), 64);
                    if (withLocation)
                        p.AddVarChar("@LocationCode", OptionalCode(r.LocationCode, "LocationCode", 20), 20);
                    p.AddVarChar("@OperatorBadge", Badge(r.OperatorBadge), 20);
                    p.AddVarChar("@StationCode", Code(r.StationCode, "StationCode"), 20);
                    p.AddGuid("@ClientScanId", Guard.NotEmpty(r.ClientScanId, "ClientScanId"));
                    p.AddDateTime2("@ScannedAtUtc", Guard.Utc(r.ScannedAtUtc));
                    p.AddNVarChar("@Reference", Guard.Optional(r.Reference, "Reference", 50), 50);
                }, Mapping.ToScanResult).ConfigureAwait(false)
                    ?? throw new InvalidOperationException(procedure + " returned no result.");
            }, DescribeScan);

        public Task<ScanEventInfo[]> GetItemHistoryAsync(string tagId) =>
            Operation.Run(nameof(GetItemHistoryAsync), tagId, async () =>
                (await Db.QueryAsync("dbo.usp_GetItemHistory",
                    p => p.AddVarChar("@TagId", Tag(tagId), 64),
                    Mapping.ToScanEvent).ConfigureAwait(false)).ToArray(),
                r => $"{r.Length} events");

        public Task<ScanEventInfo[]> GetRecentScansAsync(string? stationCode, int top) =>
            Operation.Run(nameof(GetRecentScansAsync), stationCode ?? "all stations", async () =>
                (await Db.QueryAsync("dbo.usp_GetRecentScans", p =>
                {
                    p.AddVarChar("@StationCode", OptionalCode(stationCode, nameof(stationCode), 20), 20);
                    p.AddInt("@Top", Guard.Clamp(top, 1, 500));
                }, Mapping.ToScanEvent).ConfigureAwait(false)).ToArray(),
                r => $"{r.Length} events");

        public Task<LocationInventory[]> GetInventoryByLocationAsync(string? siteCode) =>
            Operation.Run(nameof(GetInventoryByLocationAsync), siteCode ?? "all sites", async () =>
                (await Db.QueryAsync("dbo.usp_GetInventoryByLocation",
                    p => p.AddVarChar("@SiteCode", OptionalCode(siteCode, nameof(siteCode), 10), 10),
                    Mapping.ToLocationInventory).ConfigureAwait(false)).ToArray(),
                r => $"{r.Length} locations");

        public Task<ScanExceptionInfo[]> GetOpenExceptionsAsync(int top) =>
            Operation.Run(nameof(GetOpenExceptionsAsync), string.Empty, async () =>
                (await Db.QueryAsync("dbo.usp_GetScanExceptions", p =>
                {
                    p.AddBit("@UnresolvedOnly", true);
                    p.AddInt("@Top", Guard.Clamp(top, 1, 500));
                }, Mapping.ToScanException).ConfigureAwait(false)).ToArray(),
                r => $"{r.Length} open exceptions");

        // ================================================================ logistics

        public Task<SiteInfo[]> GetSitesAsync() =>
            Operation.Run(nameof(GetSitesAsync), string.Empty, async () =>
                (await Db.QueryAsync("dbo.usp_GetSites", null, Mapping.ToSite).ConfigureAwait(false)).ToArray(),
                r => $"{r.Length} sites");

        public Task<DeliveryResult> StartDeliveryAsync(StartDeliveryRequest request) =>
            Operation.Run(nameof(StartDeliveryAsync), $"{request?.TruckCode} {request?.FromSiteCode}->{request?.ToSiteCode}", async () =>
            {
                Guard.NotNull(request, nameof(request));
                var r = request!;

                var tags = (r.TagIds ?? Array.Empty<string>())
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .Select(Tag)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                if (tags.Length > 200)
                    throw new ServiceValidationException("A delivery cannot contain more than 200 tags.");

                var tagTable = new DataTable();
                tagTable.Columns.Add("TagId", typeof(string));
                foreach (var tag in tags)
                    tagTable.Rows.Add(tag);

                return await Db.QuerySingleAsync("logistics.usp_StartDelivery", p =>
                {
                    p.AddGuid("@ClientRequestId", Guard.NotEmpty(r.ClientRequestId, "ClientRequestId"));
                    p.AddVarChar("@TruckCode", Code(r.TruckCode, "TruckCode"), 20);
                    p.AddVarChar("@DriverBadge", Badge(r.DriverBadge), 20);
                    p.AddVarChar("@FromSiteCode", Code(r.FromSiteCode, "FromSiteCode", 10), 10);
                    p.AddVarChar("@ToSiteCode", Code(r.ToSiteCode, "ToSiteCode", 10), 10);
                    p.AddTable("@TagIds", "dbo.TagList", tagTable);
                    p.AddVarChar("@StationCode", OptionalCode(r.StationCode, "StationCode", 20), 20);
                    p.AddDateTime2("@DepartedAtUtc", Guard.Utc(r.DepartedAtUtc));
                }, Mapping.ToDeliveryResult).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("usp_StartDelivery returned no result.");
            }, DescribeDelivery);

        public Task<DeliveryResult> CompleteDeliveryAsync(CompleteDeliveryRequest request) =>
            Operation.Run(nameof(CompleteDeliveryAsync), $"#{request?.DeliveryId} at {request?.ArrivalLocationCode}", async () =>
            {
                Guard.NotNull(request, nameof(request));
                var r = request!;
                if (r.DeliveryId <= 0)
                    throw new ServiceValidationException("DeliveryId is required.");

                return await Db.QuerySingleAsync("logistics.usp_CompleteDelivery", p =>
                {
                    p.AddInt("@DeliveryId", r.DeliveryId);
                    p.AddVarChar("@ArrivalLocationCode", Code(r.ArrivalLocationCode, "ArrivalLocationCode"), 20);
                    p.AddVarChar("@OperatorBadge", Badge(r.OperatorBadge), 20);
                    p.AddVarChar("@StationCode", OptionalCode(r.StationCode, "StationCode", 20), 20);
                    p.AddDateTime2("@ArrivedAtUtc", Guard.Utc(r.ArrivedAtUtc));
                }, Mapping.ToDeliveryResult).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("usp_CompleteDelivery returned no result.");
            }, DescribeDelivery);

        public Task ReportPositionAsync(GpsPosition position) =>
            Operation.Run(nameof(ReportPositionAsync), $"{position?.TruckCode} {position?.Latitude:F5},{position?.Longitude:F5}", async () =>
            {
                Guard.NotNull(position, nameof(position));
                var g = position!;
                if (g.Latitude < -90 || g.Latitude > 90 || g.Longitude < -180 || g.Longitude > 180 || double.IsNaN(g.Latitude) || double.IsNaN(g.Longitude))
                    throw new ServiceValidationException("Latitude/longitude out of range.");

                await Db.ExecuteAsync("logistics.usp_RecordGpsPing", p =>
                {
                    p.AddVarChar("@TruckCode", Code(g.TruckCode, "TruckCode"), 20);
                    p.AddDecimal("@Latitude", Math.Round((decimal)g.Latitude, 6), 9, 6);
                    p.AddDecimal("@Longitude", Math.Round((decimal)g.Longitude, 6), 9, 6);
                    p.AddDecimal("@SpeedMph", g.SpeedMph == null ? (decimal?)null : Math.Round((decimal)Math.Max(0, Math.Min(999, g.SpeedMph.Value)), 1), 5, 1);
                    p.AddSmallInt("@HeadingDeg", g.HeadingDeg == null ? (short?)null : (short)(((g.HeadingDeg.Value % 360) + 360) % 360));
                    p.AddDateTime2("@RecordedAtUtc", Guard.Utc(g.RecordedAtUtc));
                }).ConfigureAwait(false);
            });

        public Task<TruckStatus[]> GetFleetStatusAsync() =>
            Operation.Run(nameof(GetFleetStatusAsync), string.Empty, async () =>
                (await Db.QueryAsync("logistics.usp_GetFleetStatus", null, Mapping.ToTruckStatus).ConfigureAwait(false)).ToArray(),
                r => $"{r.Count(t => t.DeliveryId != null)}/{r.Length} trucks on delivery");

        public Task<GpsPosition[]> GetDeliveryRouteAsync(int deliveryId) =>
            Operation.Run(nameof(GetDeliveryRouteAsync), "#" + deliveryId, async () =>
                (await Db.QueryAsync("logistics.usp_GetDeliveryRoute",
                    p => p.AddInt("@DeliveryId", deliveryId),
                    Mapping.ToGpsPosition).ConfigureAwait(false)).ToArray(),
                r => $"{r.Length} points");

        // ================================================================ telemetry

        public Task<MachineInfo[]> GetMachinesAsync() =>
            Operation.Run(nameof(GetMachinesAsync), string.Empty, async () =>
                (await Db.QueryAsync("telemetry.usp_GetMachines", null, Mapping.ToMachine).ConfigureAwait(false)).ToArray(),
                r => $"{r.Length} machines");

        public Task<int> RecordReadingsAsync(MachineReading[] readings) =>
            Operation.Run(nameof(RecordReadingsAsync), $"{readings?.Length ?? 0} readings", async () =>
            {
                if (readings == null || readings.Length == 0)
                    return 0;
                if (readings.Length > 5000)
                    throw new ServiceValidationException("Send at most 5000 readings per call.");

                var table = new DataTable();
                table.Columns.Add("MachineCode", typeof(string));
                table.Columns.Add("RecordedAtUtc", typeof(DateTime));
                table.Columns.Add("TemperatureC", typeof(decimal));
                table.Columns.Add("StatusCode", typeof(short));
                table.Columns.Add("CycleCount", typeof(long));
                table.Columns.Add("FaultCode", typeof(short));

                foreach (var reading in readings)
                {
                    if (reading == null)
                        continue;
                    table.Rows.Add(
                        Code(reading.MachineCode, "MachineCode"),
                        Guard.Utc(reading.RecordedAtUtc) ?? DateTime.UtcNow,
                        reading.TemperatureC == null ? (object)DBNull.Value : Math.Round(reading.TemperatureC.Value, 1),
                        reading.StatusCode,
                        reading.CycleCount == null ? (object)DBNull.Value : reading.CycleCount.Value,
                        reading.FaultCode);
                }

                var inserted = await Db.QuerySingleAsync("telemetry.usp_RecordMachineReadings",
                    p => p.AddTable("@Readings", "telemetry.MachineReadingList", table),
                    rec => new[] { rec.Int("RowsInserted") }).ConfigureAwait(false);

                var count = inserted?[0] ?? 0;
                if (count < table.Rows.Count)
                    Log.Warn($"RecordReadings: {table.Rows.Count - count} readings dropped (unknown machine or status code).");
                return count;
            }, n => $"{n} stored");

        public Task<MachineStatus[]> GetMachineStatusAsync() =>
            Operation.Run(nameof(GetMachineStatusAsync), string.Empty, async () =>
                (await Db.QueryAsync("telemetry.usp_GetMachineStatus", null, Mapping.ToMachineStatus).ConfigureAwait(false)).ToArray(),
                r => $"{r.Length} machines");

        // ================================================================ helpers

        /// <summary>Cleans scanner input. Malformed tags are passed through so the database logs them as UnknownTag exceptions.</summary>
        private static string Tag(string? raw)
        {
            var code = ScanCodes.Parse(raw);
            if (code.Kind == ScanCodeKind.Empty)
                throw new ServiceValidationException("TagId is required.");
            return code.Value.Length > 64 ? code.Value.Substring(0, 64) : code.Value;
        }

        private static string Badge(string? raw)
        {
            var value = Guard.Required(raw, "Badge number", 26).ToUpperInvariant();
            if (value.StartsWith(ScanCodes.BadgePrefix, StringComparison.Ordinal))
                value = value.Substring(ScanCodes.BadgePrefix.Length);
            return Guard.Required(value, "Badge number", 20);
        }

        private static string Code(string? raw, string name, int maxLength = 20) =>
            Guard.Required(raw, name, maxLength).ToUpperInvariant();

        private static string? OptionalCode(string? raw, string name, int maxLength) =>
            Guard.Optional(raw, name, maxLength)?.ToUpperInvariant();

        private static string Describe(string? station, string? tag) => $"{station} {tag}";

        private static string DescribeScan(ScanResult result) =>
            result.Outcome == ScanOutcome.Rejected
                ? $"Rejected {result.ExceptionType}: {result.Message}"
                : $"{result.Outcome}: {result.Message}";

        private static string DescribeDelivery(DeliveryResult result) =>
            result.Outcome == ScanOutcome.Rejected ? $"Rejected: {result.Message}" : $"{result.Outcome}: {result.Message}";
    }
}
