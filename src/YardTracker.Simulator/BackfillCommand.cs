using System.Data;
using System.Diagnostics;
using Microsoft.Data.SqlClient;

namespace YardTracker.Simulator;

/// <summary>
/// Discrete-event simulation of N days of yard activity, written through the same stored procedures the
/// WCF service uses (so every business rule applies). Events are processed in time order from a priority
/// queue; each event may schedule follow-ups (receive -> put away -> check out, deliver -> coat -> thread ...).
/// </summary>
internal sealed class BackfillCommand(string connectionString, int days, int seed, bool runEtl)
{
    private static readonly TimeZoneInfo Plant = TimeZoneInfo.FindSystemTimeZoneById("Central Standard Time");

    private readonly Random _rng = new(seed);
    private readonly PriorityQueue<Func<DateTime, Task>, (DateTime When, long Sequence)> _queue = new();
    private readonly List<SimItem> _items = [];
    private readonly Dictionary<string, (double Lat, double Lon)> _sites = [];
    private readonly List<SimTruck> _trucks = Catalog.Trucks.Select(t => new SimTruck(t.Code, t.PayloadLbs)).ToList();

    private SqlConnection _db = null!;
    private DateTime _endUtc;
    private long _sequence;
    private int _tagSequence = 1000;
    private int _accepted, _rejected, _duplicates, _deliveries, _pings, _readings;

    private sealed class SimItem
    {
        public required string Tag { get; init; }
        public required ProductMix Product { get; init; }
        public required string Site { get; set; }
        public string Status { get; set; } = "InYard";
        public string? Location { get; set; }
        public decimal WeightLbs { get; set; }
        public DateTime LastMovedUtc { get; set; }
        public bool Reserved { get; set; }
    }

    private sealed class SimTruck(string code, decimal payloadLbs)
    {
        public string Code { get; } = code;
        public decimal PayloadLbs { get; } = payloadLbs;
        public DateTime BusyUntilUtc { get; set; }
    }

    private sealed record ScanOutcome(string Outcome, string? ExceptionType, string? Status, string? Location, decimal WeightLbs)
    {
        public bool Accepted => Outcome == "Accepted";
    }

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        await using var db = new SqlConnection(connectionString);
        _db = db;
        await db.OpenAsync(cancellationToken);

        if (await ScalarAsync("SELECT COUNT(*) FROM dbo.Items") > 0)
        {
            Out.Error("The database already contains items. Recreate it first: .\\scripts\\Deploy-Database.ps1 -Force");
            return 1;
        }

        await LoadSitesAsync();

        _endUtc = DateTime.UtcNow.AddMinutes(-2);
        var today = TimeZoneInfo.ConvertTimeFromUtc(_endUtc, Plant).Date;
        var firstDay = today.AddDays(-days);
        Out.Accent($"Backfilling {days} days of yard activity from {firstDay:yyyy-MM-dd} (seed {seed})...");

        Schedule(ToUtc(firstDay, 6), OpeningStockAsync);
        for (var day = firstDay; day <= today; day = day.AddDays(1))
        {
            var planned = day;
            Schedule(ToUtc(planned, 0.5), _ => PlanDay(planned));
        }

        var processed = 0;
        while (_queue.TryDequeue(out var action, out var key))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await action(key.When);
            if (++processed % 1000 == 0)
                Out.Info($"  {processed:N0} events processed, simulated clock {TimeZoneInfo.ConvertTimeFromUtc(key.When, Plant):yyyy-MM-dd HH:mm}");
        }

        Out.Info("Generating machine telemetry (15-minute samples)...");
        await BackfillTelemetryAsync(ToUtc(firstDay, 0.5), cancellationToken);

        Out.Ok($"Scans accepted {_accepted:N0}, rejected {_rejected:N0}, retries deduplicated {_duplicates:N0}; " +
               $"deliveries {_deliveries:N0}, GPS pings {_pings:N0}, machine readings {_readings:N0}; " +
               $"{_items.Count(i => i.Status == "InYard"):N0} items in yard ({stopwatch.Elapsed.TotalSeconds:N0}s).");

        if (runEtl)
        {
            Out.Info("Running ETL (etl.usp_LoadDailyInventoryMovement)...");
            await using var etl = new SqlCommand("etl.usp_LoadDailyInventoryMovement", db) { CommandType = CommandType.StoredProcedure, CommandTimeout = 600 };
            await using var reader = await etl.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
                Out.Ok($"ETL run {reader["RunId"]}: {reader["RowsExtracted"]} scan events -> {reader["RowsWritten"]} summary rows over {reader["AffectedDays"]} days.");
        }

        return 0;
    }

    // ------------------------------------------------------------------ daily plan

    private Task PlanDay(DateTime day)
    {
        if (day.DayOfWeek == DayOfWeek.Sunday)
            return Task.CompletedTask;

        var saturday = day.DayOfWeek == DayOfWeek.Saturday;
        var busy = (saturday ? 0.35 : 1.0) * (0.75 + _rng.NextDouble() * 0.5);

        Repeat(Count(8.5 * busy), () => Schedule(ShiftTime(day, saturday), t => ReceiveNewAsync("NYD", t)));
        Repeat(Count(4.0 * busy), () => Schedule(ShiftTime(day, saturday), t => ReceiveNewAsync("SYD", t)));
        Repeat(Count(5.0 * busy), () => Schedule(ShiftTime(day, saturday), t => CheckOutAsync("NYD", t)));
        Repeat(Count(3.5 * busy), () => Schedule(ShiftTime(day, saturday), t => CheckOutAsync("SYD", t)));
        Repeat(Count(0.6 * busy), () => Schedule(ShiftTime(day, saturday), t => CheckOutAsync("CTP", t)));

        if (!saturday)
        {
            Repeat(Count(1.6), () => Schedule(ToUtc(day, 6.5 + _rng.NextDouble() * 7), t => PrepareDeliveryAsync("NYD", "CTP", t)));
            Repeat(Count(1.0), () => Schedule(ToUtc(day, 7.5 + _rng.NextDouble() * 7), t => PrepareDeliveryAsync("CTP", "NYD", t)));
            Repeat(Count(0.5), () => Schedule(ToUtc(day, 7.5 + _rng.NextDouble() * 7), t => PrepareDeliveryAsync("CTP", "SYD", t)));
        }

        Repeat(_rng.Next(0, 3), () => Schedule(ShiftTime(day, saturday), BadScanAsync));
        return Task.CompletedTask;
    }

    /// <summary>Go-live stock take: existing inventory registered on the first morning.</summary>
    private async Task OpeningStockAsync(DateTime start)
    {
        foreach (var (site, count) in new[] { ("NYD", 70), ("SYD", 35), ("CTP", 12) })
        {
            for (var i = 0; i < count; i++)
            {
                var product = _rng.PickProduct(site);
                var location = _rng.Pick(Catalog.StorageFor(site, product.Category));
                await ReceiveAsync(site, product, location, start.AddMinutes(i * 1.5), "STOCKTAKE");
            }
        }
    }

    // ------------------------------------------------------------------ receiving and put-away

    private async Task ReceiveNewAsync(string site, DateTime t)
    {
        var item = await ReceiveAsync(site, _rng.PickProduct(site), site + "-DOCK1", t, $"PO-{_rng.Next(40000, 49999)}");
        if (item == null)
            return;

        var roll = _rng.NextDouble();
        if (roll < 0.10)
        {
            // Failed receiving inspection (MTR mismatch, damage): QA hold, released days later.
            Schedule(Working(t.AddHours(0.5 + _rng.NextDouble() * 2)), async t2 =>
            {
                if (await MoveAsync(item, site + "-QA", t2))
                    Schedule(Working(t2.AddDays(3 + _rng.NextDouble() * 12)), t3 => PutAwayAsync(item, t3));
            });
        }
        else if (roll < 0.96)
        {
            Schedule(Working(t.AddHours(1 + _rng.NextDouble() * 7)), t2 => PutAwayAsync(item, t2));
        }
        // else: forgotten on the dock. Shows up on the overdue report.
    }

    private async Task PutAwayAsync(SimItem item, DateTime t)
    {
        if (item.Status == "InYard" && !item.Reserved)
            await MoveAsync(item, _rng.Pick(Catalog.StorageFor(item.Site, item.Product.Category)), t);
    }

    private async Task<SimItem?> ReceiveAsync(string site, ProductMix product, string location, DateTime t, string reference)
    {
        var (station, badge) = _rng.Crew(site);
        var tag = _rng.NewTag(++_tagSequence);

        var result = await ScanAsync("dbo.usp_ReceiveItem", p =>
        {
            p.AddWithValue("@TagId", tag);
            p.AddWithValue("@ProductCode", product.ProductCode);
            p.AddWithValue("@HeatNumber", _rng.HeatNumber());
            p.AddWithValue("@Pieces", _rng.Next(product.MinPieces, product.MaxPieces + 1));
            p.AddWithValue("@LocationCode", location);
            p.AddWithValue("@OperatorBadge", badge);
            p.AddWithValue("@StationCode", station);
            p.AddWithValue("@ClientScanId", Guid.NewGuid());
            p.AddWithValue("@ScannedAtUtc", t);
            p.AddWithValue("@Reference", reference);
        });

        if (!result.Accepted)
            return null;

        var item = new SimItem { Tag = tag, Product = product, Site = site, Location = location, WeightLbs = result.WeightLbs, LastMovedUtc = t };
        _items.Add(item);
        return item;
    }

    // ------------------------------------------------------------------ moves and check-outs

    private async Task<bool> MoveAsync(SimItem item, string location, DateTime t)
    {
        if (item.Status != "InYard" || item.Location == location || t > _endUtc)
            return false;

        var (station, badge) = _rng.Crew(item.Site);
        var clientScanId = Guid.NewGuid();
        void Parameters(SqlParameterCollection p)
        {
            p.AddWithValue("@TagId", item.Tag);
            p.AddWithValue("@LocationCode", location);
            p.AddWithValue("@OperatorBadge", badge);
            p.AddWithValue("@StationCode", station);
            p.AddWithValue("@ClientScanId", clientScanId);
            p.AddWithValue("@ScannedAtUtc", t);
        }

        var result = await ScanAsync("dbo.usp_MoveItem", Parameters);

        // Now and then the handheld loses Wi-Fi before the reply arrives and resends the same scan.
        if (result.Accepted && _rng.NextDouble() < 0.02)
            await ScanAsync("dbo.usp_MoveItem", Parameters);

        Apply(item, result, t);
        return result.Accepted;
    }

    private async Task CheckOutAsync(string site, DateTime t)
    {
        var candidates = _items
            .Where(i => i.Site == site && i.Status == "InYard" && !i.Reserved && Catalog.IsStorage(i.Location) && i.LastMovedUtc < t.AddHours(-20))
            .OrderBy(i => i.LastMovedUtc)
            .ToList();
        if (candidates.Count == 0)
            return;

        // Mostly FIFO (oldest heat numbers first), with some jobs needing a specific item.
        var item = _rng.NextDouble() < 0.65
            ? candidates[_rng.Next(Math.Max(1, candidates.Count / 3))]
            : _rng.Pick(candidates);
        var reference = _rng.NextDouble() < 0.7 ? $"WO-{_rng.Next(10000, 99999)}" : $"SO-{_rng.Next(200000, 299999)}";

        if (site != "CTP" && _rng.NextDouble() < 0.3)
        {
            item.Reserved = true;
            if (await MoveReservedAsync(item, site + "-STG", t))
            {
                Schedule(Working(t.AddHours(0.5 + _rng.NextDouble() * 3)), async t2 =>
                {
                    item.Reserved = false;
                    await CheckOutItemAsync(item, t2, reference, fromStaging: true);
                });
            }
            else
            {
                item.Reserved = false;
            }
            return;
        }

        await CheckOutItemAsync(item, t, reference, fromStaging: false);
    }

    private async Task CheckOutItemAsync(SimItem item, DateTime t, string reference, bool fromStaging)
    {
        if (item.Status != "InYard")
            return;

        var (station, badge) = fromStaging && item.Site == "NYD" && _rng.NextDouble() < 0.4
            ? ("KIOSK-SHIP", "2001")
            : _rng.Crew(item.Site);

        var result = await ScanAsync("dbo.usp_CheckOutItem", p =>
        {
            p.AddWithValue("@TagId", item.Tag);
            p.AddWithValue("@OperatorBadge", badge);
            p.AddWithValue("@StationCode", station);
            p.AddWithValue("@ClientScanId", Guid.NewGuid());
            p.AddWithValue("@ScannedAtUtc", t);
            p.AddWithValue("@Reference", reference);
        });
        Apply(item, result, t);

        // Some material comes back from the job unused.
        if (result.Accepted && _rng.NextDouble() < 0.08)
            Schedule(Working(t.AddDays(1 + _rng.NextDouble() * 9)), t2 => CheckInAsync(item, t2));
    }

    private async Task CheckInAsync(SimItem item, DateTime t)
    {
        if (item.Status != "CheckedOut")
            return;

        var (station, badge) = _rng.Crew(item.Site);
        var result = await ScanAsync("dbo.usp_CheckInItem", p =>
        {
            p.AddWithValue("@TagId", item.Tag);
            p.AddWithValue("@LocationCode", _rng.Pick(Catalog.StorageFor(item.Site, item.Product.Category)));
            p.AddWithValue("@OperatorBadge", badge);
            p.AddWithValue("@StationCode", station);
            p.AddWithValue("@ClientScanId", Guid.NewGuid());
            p.AddWithValue("@ScannedAtUtc", t);
            p.AddWithValue("@Reference", "RETURN");
        });
        Apply(item, result, t);
    }

    private async Task<bool> MoveReservedAsync(SimItem item, string location, DateTime t)
    {
        item.Reserved = false;
        var moved = await MoveAsync(item, location, t) || item.Location == location;
        item.Reserved = true;
        return moved;
    }

    // ------------------------------------------------------------------ truck deliveries

    private async Task PrepareDeliveryAsync(string from, string to, DateTime t)
    {
        var truck = _trucks.FirstOrDefault(tr => tr.BusyUntilUtc <= t);
        if (truck == null)
            return;

        var candidates = _items
            .Where(i => i.Site == from && i.Status == "InYard" && !i.Reserved)
            .Where(i => from == "CTP"
                ? i.Location == "CTP-FG" && i.LastMovedUtc < t.AddHours(-4)
                : Catalog.IsStorage(i.Location) && i.Product.Category is "Pipe" or "Casing" or "Tubing" && i.LastMovedUtc < t.AddDays(-2))
            .OrderBy(_ => _rng.Next())
            .ToList();

        var load = new List<SimItem>();
        var weight = 0m;
        foreach (var item in candidates)
        {
            if (load.Count == 6 || weight + item.WeightLbs > Math.Min(truck.PayloadLbs, 44000))
                continue;
            load.Add(item);
            weight += item.WeightLbs;
        }
        if (load.Count == 0)
            return;

        truck.BusyUntilUtc = t.AddHours(8);
        foreach (var item in load)
            item.Reserved = true;

        // Stage the load at the outbound bay, then depart.
        for (var i = 0; i < load.Count; i++)
            await MoveReservedAsync(load[i], from + "-STG", t.AddMinutes(i * 4));

        Schedule(t.AddHours(1.5 + _rng.NextDouble()), departure => DepartAsync(truck, from, to, load, departure));
    }

    private async Task DepartAsync(SimTruck truck, string from, string to, List<SimItem> load, DateTime t)
    {
        var onBoard = load.Where(i => i.Status == "InYard").ToList();
        var tags = new DataTable();
        tags.Columns.Add("TagId", typeof(string));
        foreach (var item in onBoard)
            tags.Rows.Add(item.Tag);

        var driver = _rng.Pick(Catalog.DriverBadges);
        int? deliveryId = null;
        string outcome = "Rejected";

        if (onBoard.Count > 0)
        {
            await using var command = new SqlCommand("logistics.usp_StartDelivery", _db) { CommandType = CommandType.StoredProcedure };
            command.Parameters.AddWithValue("@ClientRequestId", Guid.NewGuid());
            command.Parameters.AddWithValue("@TruckCode", truck.Code);
            command.Parameters.AddWithValue("@DriverBadge", driver);
            command.Parameters.AddWithValue("@FromSiteCode", from);
            command.Parameters.AddWithValue("@ToSiteCode", to);
            command.Parameters.Add(new SqlParameter("@TagIds", SqlDbType.Structured) { TypeName = "dbo.TagList", Value = tags });
            command.Parameters.AddWithValue("@StationCode", Catalog.DriverStation);
            command.Parameters.AddWithValue("@DepartedAtUtc", t);

            await using var reader = await command.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                outcome = (string)reader["Outcome"];
                deliveryId = reader["DeliveryId"] as int?;
            }
        }

        foreach (var item in load)
            item.Reserved = false;

        if (outcome != "Accepted" || deliveryId == null)
        {
            truck.BusyUntilUtc = t;
            return;
        }

        _deliveries++;
        foreach (var item in onBoard)
        {
            item.Status = "InTransit";
            item.Location = null;
            item.LastMovedUtc = t;
        }

        var (fromLat, fromLon) = _sites[from];
        var (toLat, toLon) = _sites[to];
        var miles = Geo.Miles(fromLat, fromLon, toLat, toLon) * 1.35;
        var transit = TimeSpan.FromHours(miles / 32 + 0.15 + _rng.NextDouble() * 0.3);
        var arrival = t + transit;
        truck.BusyUntilUtc = arrival.AddHours(1);

        // GPS pings every ~3 minutes along the route (only up to "now"; later trips are still on the road).
        var bend = (_rng.NextDouble() - 0.5) * 0.3;
        var steps = Math.Max(4, (int)(transit.TotalMinutes / 3));
        var previous = (Lat: fromLat, Lon: fromLon);
        for (var step = 0; step <= steps; step++)
        {
            var at = t + transit * step / steps;
            if (at > _endUtc)
                break;

            var point = Geo.Along(fromLat, fromLon, toLat, toLon, (double)step / steps, bend);
            var segmentMiles = Geo.Miles(previous.Lat, previous.Lon, point.Lat, point.Lon);
            var speed = step == 0 || step == steps ? 0 : Math.Min(65, segmentMiles / (transit.TotalHours / steps) * (0.85 + _rng.NextDouble() * 0.3));

            await using var ping = new SqlCommand("logistics.usp_RecordGpsPing", _db) { CommandType = CommandType.StoredProcedure };
            ping.Parameters.AddWithValue("@TruckCode", truck.Code);
            ping.Parameters.AddWithValue("@Latitude", Math.Round((decimal)point.Lat, 6));
            ping.Parameters.AddWithValue("@Longitude", Math.Round((decimal)point.Lon, 6));
            ping.Parameters.AddWithValue("@SpeedMph", Math.Round((decimal)speed, 1));
            ping.Parameters.AddWithValue("@HeadingDeg", (short)Geo.Bearing(previous.Lat, previous.Lon, point.Lat, point.Lon));
            ping.Parameters.AddWithValue("@RecordedAtUtc", at);
            await ping.ExecuteNonQueryAsync();
            _pings++;
            previous = point;
        }

        Schedule(arrival, arrived => ArriveAsync(deliveryId.Value, to, onBoard, arrived));
    }

    private async Task ArriveAsync(int deliveryId, string site, List<SimItem> load, DateTime t)
    {
        var (station, badge) = _rng.Crew(site);
        var dock = site + "-DOCK1";

        await using (var command = new SqlCommand("logistics.usp_CompleteDelivery", _db) { CommandType = CommandType.StoredProcedure })
        {
            command.Parameters.AddWithValue("@DeliveryId", deliveryId);
            command.Parameters.AddWithValue("@ArrivalLocationCode", dock);
            command.Parameters.AddWithValue("@OperatorBadge", badge);
            command.Parameters.AddWithValue("@StationCode", station);
            command.Parameters.AddWithValue("@ArrivedAtUtc", t);
            await using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync() || (string)reader["Outcome"] != "Accepted")
                return;
        }

        foreach (var item in load)
        {
            item.Site = site;
            item.Status = "InYard";
            item.Location = dock;
            item.LastMovedUtc = t;

            if (site == "CTP")
            {
                // Pipe goes through the coating line; casing and tubing through the threader (the slow step).
                var bay = item.Product.Category == "Pipe" ? "CTP-BAY1" : "CTP-BAY2";
                var processDays = item.Product.Category == "Pipe" ? 0.5 + _rng.NextDouble() : 1.5 + _rng.NextDouble() * 3.5;
                Schedule(Working(t.AddHours(1 + _rng.NextDouble() * 3)), async t2 =>
                {
                    if (await MoveAsync(item, bay, t2))
                        Schedule(Working(t2.AddDays(processDays)), t3 => MoveAsync(item, "CTP-FG", t3));
                });
            }
            else
            {
                Schedule(Working(t.AddHours(1 + _rng.NextDouble() * 5)), t2 => PutAwayAsync(item, t2));
            }
        }
    }

    // ------------------------------------------------------------------ exceptions

    private async Task BadScanAsync(DateTime t)
    {
        switch (_rng.Next(4))
        {
            case 0: // barcode misread
                await ScanAsync("dbo.usp_CheckOutItem", p => CommonScan(p, $"YT-9{_rng.Next(10000, 99999)}", "1001", "HH-01", t));
                break;

            case 1: // double check-out of an item that already left
                var gone = _items.Where(i => i.Status == "CheckedOut").ToList();
                if (gone.Count > 0)
                    await ScanAsync("dbo.usp_CheckOutItem", p => CommonScan(p, _rng.Pick(gone).Tag, "1002", "HH-02", t));
                break;

            case 2: // move scanned against a location at another site
                var nyd = _items.Where(i => i.Site == "NYD" && i.Status == "InYard" && !i.Reserved).ToList();
                if (nyd.Count > 0)
                    await ScanAsync("dbo.usp_MoveItem", p =>
                    {
                        CommonScan(p, _rng.Pick(nyd).Tag, "1005", "HH-01", t);
                        p.AddWithValue("@LocationCode", "SYD-R01");
                    });
                break;

            default: // badge of a former employee
                var any = _items.Where(i => i.Status == "InYard").ToList();
                if (any.Count > 0)
                    await ScanAsync("dbo.usp_CheckOutItem", p => CommonScan(p, _rng.Pick(any).Tag, "1099", "HH-03", t));
                break;
        }
    }

    private static void CommonScan(SqlParameterCollection p, string tag, string badge, string station, DateTime t)
    {
        p.AddWithValue("@TagId", tag);
        p.AddWithValue("@OperatorBadge", badge);
        p.AddWithValue("@StationCode", station);
        p.AddWithValue("@ClientScanId", Guid.NewGuid());
        p.AddWithValue("@ScannedAtUtc", t);
    }

    // ------------------------------------------------------------------ telemetry

    private async Task BackfillTelemetryAsync(DateTime startUtc, CancellationToken cancellationToken)
    {
        var models = MachineProfile.All.Select(p => new MachineModel(p, new Random(seed + p.UnitId))).ToList();
        var table = NewReadingTable();

        for (var t = startUtc; t <= _endUtc; t = t.AddMinutes(15))
        {
            var local = TimeZoneInfo.ConvertTimeFromUtc(t, Plant);
            var onShift = local.DayOfWeek != DayOfWeek.Sunday
                          && local.Hour >= 6
                          && local.Hour < (local.DayOfWeek == DayOfWeek.Saturday ? 12 : 18);
            var maintenance = local.DayOfWeek == DayOfWeek.Saturday && local.Day <= 7 && local.Hour is >= 6 and < 12;

            foreach (var model in models)
            {
                model.Step(15, onShift, maintenance);
                var s = model.Snapshot;
                table.Rows.Add(model.Profile.MachineCode, t, (decimal)s.TemperatureC, (short)s.State, (long)s.CycleCount, (short)s.FaultCode);
            }

            if (table.Rows.Count >= 2000)
            {
                await FlushReadingsAsync(table, cancellationToken);
                table = NewReadingTable();
            }
        }

        await FlushReadingsAsync(table, cancellationToken);
    }

    private static DataTable NewReadingTable()
    {
        var table = new DataTable();
        table.Columns.Add("MachineCode", typeof(string));
        table.Columns.Add("RecordedAtUtc", typeof(DateTime));
        table.Columns.Add("TemperatureC", typeof(decimal));
        table.Columns.Add("StatusCode", typeof(short));
        table.Columns.Add("CycleCount", typeof(long));
        table.Columns.Add("FaultCode", typeof(short));
        return table;
    }

    private async Task FlushReadingsAsync(DataTable table, CancellationToken cancellationToken)
    {
        if (table.Rows.Count == 0)
            return;

        await using var command = new SqlCommand("telemetry.usp_RecordMachineReadings", _db) { CommandType = CommandType.StoredProcedure };
        command.Parameters.Add(new SqlParameter("@Readings", SqlDbType.Structured) { TypeName = "telemetry.MachineReadingList", Value = table });
        _readings += Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    // ------------------------------------------------------------------ plumbing

    private async Task<ScanOutcome> ScanAsync(string procedure, Action<SqlParameterCollection> parameters)
    {
        await using var command = new SqlCommand(procedure, _db) { CommandType = CommandType.StoredProcedure };
        parameters(command.Parameters);

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            throw new InvalidOperationException(procedure + " returned no result.");

        var result = new ScanOutcome(
            (string)reader["Outcome"],
            reader["ExceptionType"] as string,
            reader["Status"] as string,
            reader["LocationCode"] as string,
            reader["WeightLbs"] is decimal w ? w : 0);

        switch (result.Outcome)
        {
            case "Accepted": _accepted++; break;
            case "Duplicate": _duplicates++; break;
            default: _rejected++; break;
        }
        return result;
    }

    private static void Apply(SimItem item, ScanOutcome result, DateTime t)
    {
        if (result.Status == null)
            return;
        item.Status = result.Status;
        item.Location = result.Location;
        if (result.Accepted)
            item.LastMovedUtc = t;
    }

    private async Task LoadSitesAsync()
    {
        await using var command = new SqlCommand("SELECT SiteCode, Latitude, Longitude FROM dbo.Sites", _db);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            _sites[(string)reader["SiteCode"]] = ((double)(decimal)reader["Latitude"], (double)(decimal)reader["Longitude"]);
    }

    private async Task<int> ScalarAsync(string sql)
    {
        await using var command = new SqlCommand(sql, _db);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private void Schedule(DateTime whenUtc, Func<DateTime, Task> action)
    {
        if (whenUtc <= _endUtc)
            _queue.Enqueue(action, (whenUtc, _sequence++));
    }

    private static void Repeat(int count, Action action)
    {
        for (var i = 0; i < count; i++)
            action();
    }

    private int Count(double mean) => Math.Max(0, (int)Math.Round(mean + (_rng.NextDouble() - 0.5) * mean * 0.6));

    private static DateTime ToUtc(DateTime localDate, double hours) =>
        TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(localDate.Date.AddHours(hours), DateTimeKind.Unspecified), Plant);

    private DateTime ShiftTime(DateTime day, bool saturday) =>
        ToUtc(day, 6 + _rng.NextDouble() * (saturday ? 5.5 : 11.5));

    /// <summary>Pushes a follow-up that lands outside working hours to the start of the next shift.</summary>
    private DateTime Working(DateTime utc)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(utc, Plant);
        var endHour = local.DayOfWeek == DayOfWeek.Saturday ? 12 : 17.5;

        if (local.DayOfWeek != DayOfWeek.Sunday && local.TimeOfDay.TotalHours >= 6 && local.TimeOfDay.TotalHours < endHour)
            return utc;

        var day = local.TimeOfDay.TotalHours < 6 ? local.Date : local.Date.AddDays(1);
        while (day.DayOfWeek == DayOfWeek.Sunday)
            day = day.AddDays(1);
        return ToUtc(day, 6 + _rng.NextDouble() * 2);
    }
}
