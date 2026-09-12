using System.Data;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace YardTracker.Tests;

/// <summary>
/// Deploys database/*.sql into a throwaway LocalDB database, so these tests exercise the real scripts and
/// stored procedures. Tests are skipped when LocalDB is not installed.
/// </summary>
public sealed partial class DatabaseFixture : IAsyncLifetime
{
    private const string DatabaseName = "YardTracker_Test";
    private const string Server = @"(localdb)\MSSQLLocalDB";

    public string ConnectionString { get; } = $"Server={Server};Database={DatabaseName};Integrated Security=true;TrustServerCertificate=true;Connect Timeout=60";
    public bool Available { get; private set; }
    public string SkipReason { get; private set; } = "Database not initialised.";

    public async ValueTask InitializeAsync()
    {
        try
        {
            await using var master = new SqlConnection($"Server={Server};Database=master;Integrated Security=true;TrustServerCertificate=true;Connect Timeout=60");
            await master.OpenAsync();
            await RunBatchesAsync(master, $"""
                IF DB_ID('{DatabaseName}') IS NOT NULL
                BEGIN
                    ALTER DATABASE [{DatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                    DROP DATABASE [{DatabaseName}];
                END
                GO
                CREATE DATABASE [{DatabaseName}];
                GO
                ALTER DATABASE [{DatabaseName}] SET READ_COMMITTED_SNAPSHOT ON;
                """);
        }
        catch (SqlException ex)
        {
            SkipReason = "SQL Server LocalDB is not available: " + ex.Message;
            return;
        }

        await using var db = new SqlConnection(ConnectionString);
        await db.OpenAsync();
        foreach (var script in Directory.GetFiles(FindDatabaseFolder(), "*.sql").Order(StringComparer.Ordinal))
            await RunBatchesAsync(db, await File.ReadAllTextAsync(script));

        Available = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (!Available)
            return;

        SqlConnection.ClearAllPools();
        await using var master = new SqlConnection($"Server={Server};Database=master;Integrated Security=true;TrustServerCertificate=true");
        await master.OpenAsync();
        await RunBatchesAsync(master, $"ALTER DATABASE [{DatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{DatabaseName}];");
    }

    private static async Task RunBatchesAsync(SqlConnection connection, string sql)
    {
        foreach (var batch in GoSeparator().Split(sql).Where(b => !string.IsNullOrWhiteSpace(b)))
        {
            await using var command = new SqlCommand(batch, connection) { CommandTimeout = 300 };
            await command.ExecuteNonQueryAsync();
        }
    }

    private static string FindDatabaseFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "database");
            if (File.Exists(Path.Combine(candidate, "01_schema.sql")))
                return candidate;
        }
        throw new DirectoryNotFoundException("Could not find the database/ folder above " + AppContext.BaseDirectory);
    }

    [GeneratedRegex(@"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex GoSeparator();
}

public sealed class StoredProcedureTests(DatabaseFixture db) : IClassFixture<DatabaseFixture>
{
    private static readonly TimeZoneInfo Plant = TimeZoneInfo.FindSystemTimeZoneById("Central Standard Time");

    [Fact]
    public async Task Retried_scan_with_the_same_client_scan_id_is_recorded_once()
    {
        RequireDatabase();
        var tag = NewTag();
        var clientScanId = Guid.NewGuid();

        var first = await ReceiveAsync(tag, clientScanId: clientScanId);
        var retry = await ReceiveAsync(tag, clientScanId: clientScanId);

        Assert.Equal("Accepted", first["Outcome"]);
        Assert.Equal("Duplicate", retry["Outcome"]);
        Assert.Equal(first["ScanEventId"], retry["ScanEventId"]);
        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.ScanEvents se JOIN dbo.Items i ON i.ItemId = se.ItemId WHERE i.TagId = @p0", tag));
    }

    [Fact]
    public async Task Check_out_and_check_in_follow_the_item_state_machine()
    {
        RequireDatabase();
        var tag = NewTag();
        await ReceiveAsync(tag);

        var checkedOut = await ScanAsync("dbo.usp_CheckOutItem", tag, reference: "WO-1");
        var doubleCheckOut = await ScanAsync("dbo.usp_CheckOutItem", tag);
        var checkedIn = await ScanAsync("dbo.usp_CheckInItem", tag, location: "NYD-R01");

        Assert.Equal(("Accepted", "CheckedOut"), (checkedOut["Outcome"], checkedOut["Status"]));
        Assert.Equal(DBNull.Value, checkedOut["LocationCode"]);
        Assert.Equal(("Rejected", "InvalidState"), (doubleCheckOut["Outcome"], doubleCheckOut["ExceptionType"]));
        Assert.Equal(("Accepted", "InYard", "NYD-R01"), (checkedIn["Outcome"], checkedIn["Status"], checkedIn["LocationCode"]));
        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.ScanExceptions WHERE RawTag = @p0 AND ExceptionType = 'InvalidState'", tag));
    }

    [Fact]
    public async Task Unknown_tag_is_rejected_and_logged_for_review()
    {
        RequireDatabase();
        var tag = NewTag();

        var result = await ScanAsync("dbo.usp_CheckOutItem", tag);

        Assert.Equal(("Rejected", "UnknownTag"), (result["Outcome"], result["ExceptionType"]));
        Assert.Equal(DBNull.Value, result["ItemId"]);
        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.ScanExceptions WHERE RawTag = @p0", tag));
    }

    [Fact]
    public async Task Moves_must_stay_on_site_and_change_location()
    {
        RequireDatabase();
        var tag = NewTag();
        await ReceiveAsync(tag, location: "NYD-R02");

        var otherSite = await ScanAsync("dbo.usp_MoveItem", tag, location: "SYD-R01");
        var sameLocation = await ScanAsync("dbo.usp_MoveItem", tag, location: "NYD-R02");
        var valid = await ScanAsync("dbo.usp_MoveItem", tag, location: "NYD-LD-A");

        Assert.Equal("WrongSite", otherSite["ExceptionType"]);
        Assert.Equal("InvalidState", sameLocation["ExceptionType"]);
        Assert.Equal(("Accepted", "NYD-LD-A"), (valid["Outcome"], valid["LocationCode"]));
    }

    [Fact]
    public async Task Delivery_puts_items_in_transit_and_unloads_only_at_the_destination_site()
    {
        RequireDatabase();
        var tags = new[] { NewTag(), NewTag() };
        foreach (var tag in tags)
            await ReceiveAsync(tag, location: "NYD-R01");

        var tagTable = new DataTable();
        tagTable.Columns.Add("TagId", typeof(string));
        foreach (var tag in tags)
            tagTable.Rows.Add(tag);

        var started = await ExecAsync("logistics.usp_StartDelivery",
            ("@ClientRequestId", Guid.NewGuid()), ("@TruckCode", "TRK-102"), ("@DriverBadge", "3001"),
            ("@FromSiteCode", "NYD"), ("@ToSiteCode", "CTP"),
            ("@TagIds", new SqlParameter("@TagIds", SqlDbType.Structured) { TypeName = "dbo.TagList", Value = tagTable }));
        var deliveryId = Convert.ToInt32(started["DeliveryId"]);

        await QueryAsync("logistics.usp_RecordGpsPing",("@TruckCode", "TRK-102"), ("@Latitude", 29.85m), ("@Longitude", -95.2m), ("@SpeedMph", 41.5m), ("@HeadingDeg", (short)120));
        var inTransit = await ExecAsync("dbo.usp_GetItemByTag", ("@TagId", tags[0]));

        var wrongSite = await CompleteAsync(deliveryId, "NYD-DOCK1");
        var unloaded = await CompleteAsync(deliveryId, "CTP-DOCK1");
        var again = await CompleteAsync(deliveryId, "CTP-DOCK1");
        var arrived = await ExecAsync("dbo.usp_GetItemByTag", ("@TagId", tags[1]));

        Assert.Equal(("Accepted", 2), (started["Outcome"], Convert.ToInt32(started["ItemCount"])));
        Assert.Equal("InTransit", inTransit["Status"]);
        Assert.Equal(deliveryId, await ScalarAsync<int>("SELECT TOP (1) DeliveryId FROM logistics.GpsPings ORDER BY GpsPingId DESC"));
        Assert.Equal("Rejected", wrongSite["Outcome"]);
        Assert.Equal("Accepted", unloaded["Outcome"]);
        Assert.Equal("Duplicate", again["Outcome"]);
        Assert.Equal(("InYard", "CTP-DOCK1"), (arrived["Status"], arrived["LocationCode"]));
    }

    [Fact]
    public async Task Etl_is_incremental_and_rebuilds_days_touched_by_late_arriving_scans()
    {
        RequireDatabase();
        await ExecAsync("etl.usp_LoadDailyInventoryMovement"); // catch up on anything other tests wrote

        // A handheld was offline two days ago; its scans only reach the server now.
        var scannedAt = DateTime.UtcNow.Date.AddDays(-2).AddHours(15);
        var businessDate = TimeZoneInfo.ConvertTimeFromUtc(scannedAt, Plant).Date;

        await ReceiveAsync(NewTag(), location: "SYD-QA", scannedAt: scannedAt);
        var firstRun = await ExecAsync("etl.usp_LoadDailyInventoryMovement");
        var receiptsAfterFirst = await ReceiptsAsync("SYD-QA", businessDate);

        await ReceiveAsync(NewTag(), location: "SYD-QA", scannedAt: scannedAt.AddMinutes(20));
        var secondRun = await ExecAsync("etl.usp_LoadDailyInventoryMovement");
        var receiptsAfterSecond = await ReceiptsAsync("SYD-QA", businessDate);

        var emptyRun = await ExecAsync("etl.usp_LoadDailyInventoryMovement");

        Assert.Equal(1, Convert.ToInt32(firstRun["RowsExtracted"]));
        Assert.Equal(1, receiptsAfterFirst);
        Assert.Equal(1, Convert.ToInt32(secondRun["RowsExtracted"]));
        Assert.Equal(2, receiptsAfterSecond); // rebuilt, not double counted
        Assert.Equal(0, Convert.ToInt32(emptyRun["RowsExtracted"]));
        Assert.Equal(0, await ScalarAsync<int>("SELECT COUNT(*) FROM etl.RunLog WHERE Status <> 'Succeeded'"));
    }

    [Fact]
    public async Task Overdue_report_grades_items_by_days_idle()
    {
        RequireDatabase();
        var critical = NewTag();
        var overdue = NewTag();
        await ReceiveAsync(critical, location: "SYD-R02", scannedAt: DateTime.UtcNow.AddDays(-70));
        await ReceiveAsync(overdue, location: "SYD-R02", scannedAt: DateTime.UtcNow.AddDays(-40));

        var report = await QueryAsync("rpt.usp_ItemsOverdueForMovement", ("@DaysThreshold", 30), ("@SiteCode", "SYD"));
        string SeverityOf(string tag) => (string)report.AsEnumerable().Single(r => (string)r["TagId"] == tag)["Severity"];

        Assert.Equal("Critical", SeverityOf(critical));
        Assert.Equal("Overdue", SeverityOf(overdue));
    }

    [Fact]
    public async Task Telemetry_for_unknown_machines_is_dropped()
    {
        RequireDatabase();
        var readings = new DataTable();
        readings.Columns.Add("MachineCode", typeof(string));
        readings.Columns.Add("RecordedAtUtc", typeof(DateTime));
        readings.Columns.Add("TemperatureC", typeof(decimal));
        readings.Columns.Add("StatusCode", typeof(short));
        readings.Columns.Add("CycleCount", typeof(long));
        readings.Columns.Add("FaultCode", typeof(short));
        readings.Rows.Add("OVN-C1", DateTime.UtcNow, 231.7m, (short)2, 1200L, (short)0);
        readings.Rows.Add("NOT-A-MACHINE", DateTime.UtcNow, 20m, (short)1, 0L, (short)0);

        var inserted = await ExecAsync("telemetry.usp_RecordMachineReadings",
            ("@Readings", new SqlParameter("@Readings", SqlDbType.Structured) { TypeName = "telemetry.MachineReadingList", Value = readings }));
        var status = await QueryAsync("telemetry.usp_GetMachineStatus");

        Assert.Equal(1, Convert.ToInt32(inserted["RowsInserted"]));
        var oven = status.AsEnumerable().Single(r => (string)r["MachineCode"] == "OVN-C1");
        Assert.Equal(("Running", 231.7m), (oven["StatusName"], oven["TemperatureC"]));
    }

    // ------------------------------------------------------------------ helpers

    private void RequireDatabase() => Assert.SkipUnless(db.Available, db.SkipReason);

    private static string NewTag() => "T-" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();

    private Task<DataRow> ReceiveAsync(string tag, string location = "NYD-DOCK1", DateTime? scannedAt = null, Guid? clientScanId = null) =>
        ExecAsync("dbo.usp_ReceiveItem",
            ("@TagId", tag), ("@ProductCode", "PIPE-X52-1275"), ("@HeatNumber", "H12345"), ("@Pieces", 4),
            ("@LocationCode", location), ("@OperatorBadge", "1001"), ("@StationCode", "HH-01"),
            ("@ClientScanId", clientScanId ?? Guid.NewGuid()), ("@ScannedAtUtc", scannedAt));

    private Task<DataRow> ScanAsync(string procedure, string tag, string? location = null, string? reference = null)
    {
        var parameters = new List<(string, object?)>
        {
            ("@TagId", tag), ("@OperatorBadge", "1002"), ("@StationCode", "HH-02"), ("@ClientScanId", Guid.NewGuid()), ("@Reference", reference)
        };
        if (procedure != "dbo.usp_CheckOutItem")
            parameters.Add(("@LocationCode", location));
        return ExecAsync(procedure, [.. parameters]);
    }

    private Task<DataRow> CompleteAsync(int deliveryId, string location) =>
        ExecAsync("logistics.usp_CompleteDelivery", ("@DeliveryId", deliveryId), ("@ArrivalLocationCode", location), ("@OperatorBadge", "1004"), ("@StationCode", "HH-04"));

    private Task<int> ReceiptsAsync(string locationCode, DateTime businessDate) =>
        ScalarAsync<int>("""
            SELECT ISNULL(SUM(m.Receipts), 0)
            FROM rpt.DailyInventoryMovement AS m
            JOIN dbo.Locations AS l ON l.LocationId = m.LocationId
            WHERE l.LocationCode = @p0 AND m.BusinessDate = @p1
            """, locationCode, businessDate.Date);

    private async Task<DataRow> ExecAsync(string procedure, params (string Name, object? Value)[] parameters)
    {
        var table = await QueryAsync(procedure, parameters);
        Assert.True(table.Rows.Count > 0, procedure + " returned no rows");
        return table.Rows[0];
    }

    private async Task<DataTable> QueryAsync(string procedure, params (string Name, object? Value)[] parameters)
    {
        await using var connection = new SqlConnection(db.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(procedure, connection) { CommandType = CommandType.StoredProcedure };
        foreach (var (name, value) in parameters)
            command.Parameters.Add(value as SqlParameter ?? new SqlParameter(name, value ?? DBNull.Value));

        var table = new DataTable();
        using var adapter = new SqlDataAdapter(command);
        adapter.Fill(table);
        return table;
    }

    private async Task<T> ScalarAsync<T>(string sql, params object[] args)
    {
        await using var connection = new SqlConnection(db.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        for (var i = 0; i < args.Length; i++)
            command.Parameters.AddWithValue("@p" + i, args[i]);
        return (T)Convert.ChangeType((await command.ExecuteScalarAsync())!, typeof(T));
    }
}
