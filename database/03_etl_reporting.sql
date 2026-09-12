/* =============================================================================
   YardTracker — 03_etl_reporting.sql
   ETL procedures (called by the SSIS package, or directly without SSIS)
   and the reporting layer used by SSRS and Power BI.

   Incremental load design
     * Watermark = dbo.ScanEvents.RowVer (rowversion), not ScannedAtUtc.
       Handhelds buffer scans while out of Wi-Fi range, so device timestamps
       arrive late and out of order. Rowversion follows commit order instead.
     * Upper bound = MIN_ACTIVE_ROWVERSION() - 1, so a transaction that is still
       open when the extract runs cannot be skipped.
     * The merge rebuilds every (business day, location) cell touched by the new
       events from the full event log. A scan arriving two days late correctly
       updates that older day instead of being lost or double counted.
   ============================================================================= */
SET XACT_ABORT ON;
GO

CREATE OR ALTER PROCEDURE rpt.usp_EnsureDimDate
    @FromDate  date,
    @ToDate    date
AS
BEGIN
    SET NOCOUNT ON;
    IF @FromDate IS NULL OR @ToDate IS NULL OR @FromDate > @ToDate
        RETURN;

    WITH numbers AS (
        SELECT TOP (DATEDIFF(DAY, @FromDate, @ToDate) + 1)
               ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1 AS n
        FROM sys.all_objects AS a
        CROSS JOIN sys.all_objects AS b
    )
    INSERT rpt.DimDate (DateKey, [Date], [Year], [Quarter], MonthNumber, MonthName, YearMonth, IsoWeek, DayOfWeekNumber, DayName, IsWeekend)
    SELECT
        CONVERT(int, CONVERT(char(8), d.[Date], 112)),
        d.[Date],
        YEAR(d.[Date]),
        DATEPART(QUARTER, d.[Date]),
        MONTH(d.[Date]),
        LEFT(DATENAME(MONTH, d.[Date]), 10),
        CONVERT(char(7), d.[Date], 120),
        DATEPART(ISO_WEEK, d.[Date]),
        w.DayOfWeekNumber,
        LEFT(DATENAME(WEEKDAY, d.[Date]), 10),
        CASE WHEN w.DayOfWeekNumber IN (6, 7) THEN 1 ELSE 0 END
    FROM numbers
    CROSS APPLY (SELECT DATEADD(DAY, numbers.n, @FromDate) AS [Date]) AS d
    CROSS APPLY (SELECT (DATEDIFF(DAY, '19000101', d.[Date]) % 7) + 1 AS DayOfWeekNumber) AS w   -- 1900-01-01 was a Monday
    WHERE NOT EXISTS (SELECT 1 FROM rpt.DimDate AS x WHERE x.[Date] = d.[Date]);
END
GO

/* ----------------------------------------------------------------------------
   ETL
   ---------------------------------------------------------------------------- */
CREATE OR ALTER FUNCTION etl.fn_ExtractScanEvents
(
    @FromRowVersion  bigint,   -- exclusive
    @ToRowVersion    bigint    -- inclusive
)
RETURNS TABLE
AS
RETURN
    SELECT
        se.ScanEventId,
        CAST(se.RowVer AS bigint) AS RowVersion,
        se.ItemId,
        se.EventType,
        se.FromLocationId,
        se.ToLocationId,
        se.OperatorId,
        se.ScannedAtUtc,
        se.Pieces,
        se.WeightLbs,
        p.Category AS ProductCategory
    FROM dbo.ScanEvents AS se
    JOIN dbo.Items AS i ON i.ItemId = se.ItemId
    JOIN dbo.Products AS p ON p.ProductId = i.ProductId
    WHERE se.RowVer >  CAST(@FromRowVersion AS binary(8))
      AND se.RowVer <= CAST(@ToRowVersion AS binary(8));
GO

CREATE OR ALTER PROCEDURE etl.usp_BeginRun
    @ProcessName      varchar(50) = 'DailyInventoryMovement',
    @RunSource        varchar(20) = 'SSIS',
    @ReturnResultSet  bit         = 1,
    @RunId            int         = NULL OUTPUT,
    @FromRowVersion   bigint      = NULL OUTPUT,
    @ToRowVersion     bigint      = NULL OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @now datetime2(3) = SYSUTCDATETIME(), @lockResult int;

    BEGIN TRANSACTION;

    EXEC @lockResult = sp_getapplock @Resource = 'etl.BeginRun', @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 10000;
    IF @lockResult < 0
        THROW 50100, 'Could not acquire the ETL start lock.', 1;

    IF EXISTS (SELECT 1 FROM etl.RunLog
               WHERE ProcessName = @ProcessName AND Status = 'Running' AND StartedAtUtc > DATEADD(MINUTE, -30, @now))
        THROW 50101, 'Another run of this ETL process is still in progress.', 1;

    UPDATE etl.RunLog
    SET Status = 'Failed', EndedAtUtc = @now, ErrorMessage = N'Abandoned: no completion within 30 minutes.'
    WHERE ProcessName = @ProcessName AND Status = 'Running';

    SELECT @FromRowVersion = LastRowVersion
    FROM etl.Watermarks WITH (UPDLOCK, HOLDLOCK)
    WHERE ProcessName = @ProcessName;

    IF @FromRowVersion IS NULL
    BEGIN
        SET @FromRowVersion = 0;
        INSERT etl.Watermarks (ProcessName, LastRowVersion, UpdatedAtUtc) VALUES (@ProcessName, 0, @now);
    END

    -- Every row below the oldest still-open rowversion is committed and final.
    SET @ToRowVersion = CAST(MIN_ACTIVE_ROWVERSION() AS bigint) - 1;
    IF @ToRowVersion < @FromRowVersion
        SET @ToRowVersion = @FromRowVersion;

    INSERT etl.RunLog (ProcessName, RunSource, Status, StartedAtUtc, FromRowVersion, ToRowVersion)
    VALUES (@ProcessName, @RunSource, 'Running', @now, @FromRowVersion, @ToRowVersion);

    SET @RunId = SCOPE_IDENTITY();

    COMMIT TRANSACTION;

    IF @ReturnResultSet = 1
        SELECT @RunId AS RunId, @FromRowVersion AS FromRowVersion, @ToRowVersion AS ToRowVersion;
END
GO

CREATE OR ALTER PROCEDURE etl.usp_FailRun
    @RunId         int,
    @ErrorMessage  nvarchar(2000)
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE etl.RunLog
    SET Status = 'Failed', EndedAtUtc = SYSUTCDATETIME(), ErrorMessage = LEFT(@ErrorMessage, 2000)
    WHERE RunId = @RunId AND Status = 'Running';
END
GO

/* Transform + load step. Expects etl.stg_ScanEvents to hold the extract for @RunId. */
CREATE OR ALTER PROCEDURE etl.usp_MergeDailyInventoryMovement
    @RunId int
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE
        @processName    varchar(50),
        @toRowVersion   bigint,
        @status         varchar(10),
        @now            datetime2(3) = SYSUTCDATETIME(),
        @rowsExtracted  int,
        @rowsWritten    int,
        @affectedDays   int,
        @minDate        date,
        @maxDate        date;

    SELECT @processName = ProcessName, @toRowVersion = ToRowVersion, @status = Status
    FROM etl.RunLog
    WHERE RunId = @RunId;

    IF @status IS NULL      THROW 50102, 'Unknown ETL RunId.', 1;
    IF @status <> 'Running' THROW 50103, 'ETL run is not in the Running state.', 1;

    SELECT @rowsExtracted = COUNT(*) FROM etl.stg_ScanEvents;

    -- Each event touches the location it left and the location it arrived at, on that site's local business day.
    CREATE TABLE #touched
    (
        BusinessDate  date NOT NULL,
        LocationId    int  NOT NULL,
        PRIMARY KEY (BusinessDate, LocationId)
    );

    INSERT #touched (BusinessDate, LocationId)
    SELECT DISTINCT
        CONVERT(date, (s.ScannedAtUtc AT TIME ZONE 'UTC') AT TIME ZONE st.TimeZoneId),
        v.LocationId
    FROM etl.stg_ScanEvents AS s
    CROSS APPLY (VALUES (s.FromLocationId), (s.ToLocationId)) AS v (LocationId)
    JOIN dbo.Locations AS l ON l.LocationId = v.LocationId
    JOIN dbo.Sites AS st ON st.SiteId = l.SiteId;

    SELECT @affectedDays = COUNT(DISTINCT BusinessDate), @minDate = MIN(BusinessDate), @maxDate = MAX(BusinessDate)
    FROM #touched;

    SELECT
        t.BusinessDate,
        t.LocationId,
        e.ProductCategory,
        SUM(CASE WHEN e.Direction = 'In'  AND e.EventType = 'Receive'   THEN 1 ELSE 0 END) AS Receipts,
        SUM(CASE WHEN e.Direction = 'In'  AND e.EventType = 'CheckIn'   THEN 1 ELSE 0 END) AS CheckIns,
        SUM(CASE WHEN e.Direction = 'Out' AND e.EventType = 'CheckOut'  THEN 1 ELSE 0 END) AS CheckOuts,
        SUM(CASE WHEN e.Direction = 'In'  AND e.EventType = 'Move'      THEN 1 ELSE 0 END) AS MovesIn,
        SUM(CASE WHEN e.Direction = 'Out' AND e.EventType = 'Move'      THEN 1 ELSE 0 END) AS MovesOut,
        SUM(CASE WHEN e.Direction = 'Out' AND e.EventType = 'LoadTruck' THEN 1 ELSE 0 END) AS TruckLoads,
        SUM(CASE WHEN e.Direction = 'In'  AND e.EventType = 'Deliver'   THEN 1 ELSE 0 END) AS TruckDeliveries,
        SUM(CASE WHEN e.Direction = 'In'  THEN 1 ELSE 0 END) AS InboundEvents,
        SUM(CASE WHEN e.Direction = 'Out' THEN 1 ELSE 0 END) AS OutboundEvents,
        CAST(SUM(CASE WHEN e.Direction = 'In'  THEN e.WeightLbs ELSE 0 END) / 2000.0 AS decimal(12,3)) AS InboundTons,
        CAST(SUM(CASE WHEN e.Direction = 'Out' THEN e.WeightLbs ELSE 0 END) / 2000.0 AS decimal(12,3)) AS OutboundTons,
        COUNT(DISTINCT e.ItemId)     AS DistinctItems,
        COUNT(DISTINCT e.OperatorId) AS DistinctOperators,
        MIN(e.ScannedAtUtc)          AS FirstScanAtUtc,
        MAX(e.ScannedAtUtc)          AS LastScanAtUtc
    INTO #movement
    FROM (
        SELECT
            se.ItemId, se.OperatorId, se.EventType, se.ScannedAtUtc, se.WeightLbs,
            p.Category AS ProductCategory,
            v.Direction, v.LocationId,
            CONVERT(date, (se.ScannedAtUtc AT TIME ZONE 'UTC') AT TIME ZONE st.TimeZoneId) AS BusinessDate
        FROM dbo.ScanEvents AS se
        JOIN dbo.Items AS i ON i.ItemId = se.ItemId
        JOIN dbo.Products AS p ON p.ProductId = i.ProductId
        CROSS APPLY (VALUES ('Out', se.FromLocationId), ('In', se.ToLocationId)) AS v (Direction, LocationId)
        JOIN dbo.Locations AS l ON l.LocationId = v.LocationId
        JOIN dbo.Sites AS st ON st.SiteId = l.SiteId
        WHERE @minDate IS NOT NULL
          AND se.ScannedAtUtc >= DATEADD(DAY, -1, CAST(@minDate AS datetime2(3)))
          AND se.ScannedAtUtc <  DATEADD(DAY,  2, CAST(@maxDate AS datetime2(3)))
          AND se.RowVer <= CAST(@toRowVersion AS binary(8))
    ) AS e
    JOIN #touched AS t ON t.BusinessDate = e.BusinessDate AND t.LocationId = e.LocationId
    GROUP BY t.BusinessDate, t.LocationId, e.ProductCategory;

    EXEC rpt.usp_EnsureDimDate @minDate, @maxDate;

    BEGIN TRANSACTION;

    -- Delete + insert of the touched cells. Simpler to reason about than MERGE and just as atomic.
    DELETE m
    FROM rpt.DailyInventoryMovement AS m
    JOIN #touched AS t ON t.BusinessDate = m.BusinessDate AND t.LocationId = m.LocationId;

    INSERT rpt.DailyInventoryMovement
        (BusinessDate, LocationId, ProductCategory, Receipts, CheckIns, CheckOuts, MovesIn, MovesOut, TruckLoads, TruckDeliveries,
         InboundEvents, OutboundEvents, InboundTons, OutboundTons, DistinctItems, DistinctOperators, FirstScanAtUtc, LastScanAtUtc,
         LoadedByRunId, LoadedAtUtc)
    SELECT BusinessDate, LocationId, ProductCategory, Receipts, CheckIns, CheckOuts, MovesIn, MovesOut, TruckLoads, TruckDeliveries,
           InboundEvents, OutboundEvents, InboundTons, OutboundTons, DistinctItems, DistinctOperators, FirstScanAtUtc, LastScanAtUtc,
           @RunId, @now
    FROM #movement;

    SET @rowsWritten = @@ROWCOUNT;

    UPDATE etl.Watermarks
    SET LastRowVersion = @toRowVersion, UpdatedAtUtc = @now
    WHERE ProcessName = @processName;

    UPDATE etl.RunLog
    SET Status = 'Succeeded', EndedAtUtc = SYSUTCDATETIME(),
        RowsExtracted = @rowsExtracted, RowsWritten = @rowsWritten, AffectedDays = @affectedDays
    WHERE RunId = @RunId;

    COMMIT TRANSACTION;

    SELECT @RunId AS RunId, @rowsExtracted AS RowsExtracted, @rowsWritten AS RowsWritten, ISNULL(@affectedDays, 0) AS AffectedDays;
END
GO

/* Whole pipeline in T-SQL, for running the ETL without SSIS (or from a SQL Agent job). */
CREATE OR ALTER PROCEDURE etl.usp_LoadDailyInventoryMovement
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @runId int, @from bigint, @to bigint;

    EXEC etl.usp_BeginRun
        @ProcessName = 'DailyInventoryMovement', @RunSource = 'StoredProc', @ReturnResultSet = 0,
        @RunId = @runId OUTPUT, @FromRowVersion = @from OUTPUT, @ToRowVersion = @to OUTPUT;

    BEGIN TRY
        TRUNCATE TABLE etl.stg_ScanEvents;

        INSERT etl.stg_ScanEvents
            (ScanEventId, RowVersion, ItemId, EventType, FromLocationId, ToLocationId, OperatorId, ScannedAtUtc, Pieces, WeightLbs, ProductCategory)
        SELECT ScanEventId, RowVersion, ItemId, EventType, FromLocationId, ToLocationId, OperatorId, ScannedAtUtc, Pieces, WeightLbs, ProductCategory
        FROM etl.fn_ExtractScanEvents(@from, @to);

        EXEC etl.usp_MergeDailyInventoryMovement @RunId = @runId;
    END TRY
    BEGIN CATCH
        DECLARE @error nvarchar(2000) = ERROR_MESSAGE();
        IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
        EXEC etl.usp_FailRun @RunId = @runId, @ErrorMessage = @error;
        THROW;
    END CATCH
END
GO

/* ----------------------------------------------------------------------------
   Reporting views (Power BI star schema)
   ---------------------------------------------------------------------------- */
CREATE OR ALTER VIEW rpt.vw_DimLocation
AS
SELECT l.LocationId, l.LocationCode, l.Name AS LocationName, l.LocationType, l.CapacityTons,
       s.SiteId, s.SiteCode, s.Name AS SiteName, s.Latitude AS SiteLatitude, s.Longitude AS SiteLongitude
FROM dbo.Locations AS l
JOIN dbo.Sites AS s ON s.SiteId = l.SiteId;
GO

CREATE OR ALTER VIEW rpt.vw_DimProduct
AS
SELECT ProductId, ProductCode, Category, Description, Grade, OuterDiameterIn, WallThicknessIn, NominalLengthFt, WeightLbsPerFt
FROM dbo.Products;
GO

CREATE OR ALTER VIEW rpt.vw_DimMachine
AS
SELECT m.MachineId, m.MachineCode, m.Name AS MachineName, m.MachineType, m.LocationId, m.ModbusUnitId, m.TempWarnC, m.TempAlarmC
FROM telemetry.Machines AS m;
GO

CREATE OR ALTER VIEW rpt.vw_FactDailyMovement
AS
SELECT
    CONVERT(int, CONVERT(char(8), m.BusinessDate, 112)) AS DateKey,
    m.BusinessDate, m.LocationId, m.ProductCategory,
    m.Receipts, m.CheckIns, m.CheckOuts, m.MovesIn, m.MovesOut, m.TruckLoads, m.TruckDeliveries,
    m.InboundEvents, m.OutboundEvents, m.InboundTons, m.OutboundTons,
    m.DistinctItems, m.DistinctOperators
FROM rpt.DailyInventoryMovement AS m;
GO

CREATE OR ALTER VIEW rpt.vw_CurrentInventory
AS
SELECT
    i.ItemId, i.TagId, i.ProductId, i.HeatNumber, i.Status,
    i.CurrentLocationId AS LocationId,
    i.Pieces,
    CAST(i.WeightLbs / 2000.0 AS decimal(12,3)) AS WeightTons,
    i.LastMovedAtUtc,
    a.DaysSinceLastMove,
    CASE WHEN a.DaysSinceLastMove < 7  THEN '0-6 days'
         WHEN a.DaysSinceLastMove < 30 THEN '7-29 days'
         WHEN a.DaysSinceLastMove < 90 THEN '30-89 days'
         ELSE '90+ days' END AS AgingBucket,
    CASE WHEN a.DaysSinceLastMove < 7  THEN 1
         WHEN a.DaysSinceLastMove < 30 THEN 2
         WHEN a.DaysSinceLastMove < 90 THEN 3
         ELSE 4 END AS AgingBucketSort
FROM dbo.Items AS i
CROSS APPLY (SELECT DATEDIFF(HOUR, i.LastMovedAtUtc, SYSUTCDATETIME()) / 24 AS DaysSinceLastMove) AS a;
GO

/* One row per stay of an item at a location. Every event after an arrival is a departure
   (the scan state machine guarantees it), so LEAD() gives the departure time. */
CREATE OR ALTER VIEW rpt.vw_LocationStays
AS
WITH ordered AS (
    SELECT se.ItemId, se.ToLocationId, se.ScannedAtUtc,
           LEAD(se.ScannedAtUtc) OVER (PARTITION BY se.ItemId ORDER BY se.ScannedAtUtc, se.ScanEventId) AS NextScanAtUtc
    FROM dbo.ScanEvents AS se
)
SELECT
    o.ItemId,
    o.ToLocationId AS LocationId,
    o.ScannedAtUtc AS ArrivedAtUtc,
    o.NextScanAtUtc AS DepartedAtUtc,
    CAST(CASE WHEN o.NextScanAtUtc IS NULL THEN 1 ELSE 0 END AS bit) AS IsOpen,
    CAST(DATEDIFF(MINUTE, o.ScannedAtUtc, ISNULL(o.NextScanAtUtc, SYSUTCDATETIME())) / 60.0 AS decimal(10,2)) AS DwellHours,
    CONVERT(int, CONVERT(char(8), CONVERT(date, (o.ScannedAtUtc AT TIME ZONE 'UTC') AT TIME ZONE s.TimeZoneId), 112)) AS ArrivalDateKey
FROM ordered AS o
JOIN dbo.Locations AS l ON l.LocationId = o.ToLocationId
JOIN dbo.Sites AS s ON s.SiteId = l.SiteId;
GO

CREATE OR ALTER VIEW rpt.vw_MachineReadingsHourly
AS
SELECT
    r.MachineId,
    h.HourStartUtc,
    CONVERT(int, CONVERT(char(8), CONVERT(date, (h.HourStartUtc AT TIME ZONE 'UTC') AT TIME ZONE 'Central Standard Time'), 112)) AS DateKey,
    COUNT(*)                                                        AS Samples,
    CAST(AVG(r.TemperatureC) AS decimal(6,1))                       AS AvgTemperatureC,
    MAX(r.TemperatureC)                                             AS MaxTemperatureC,
    CAST(AVG(CASE WHEN r.StatusCode = 2 THEN 100.0 ELSE 0 END) AS decimal(5,1)) AS RunningPct,
    SUM(CASE WHEN r.StatusCode = 3 THEN 1 ELSE 0 END)               AS FaultSamples,
    MAX(r.CycleCount)                                               AS MaxCycleCount
FROM telemetry.MachineReadings AS r
CROSS APPLY (SELECT DATEADD(HOUR, DATEDIFF(HOUR, CAST('19000101' AS datetime2(0)), r.RecordedAtUtc), CAST('19000101' AS datetime2(0))) AS HourStartUtc) AS h
GROUP BY r.MachineId, h.HourStartUtc;
GO

CREATE OR ALTER VIEW rpt.vw_Deliveries
AS
SELECT
    d.DeliveryId, t.TruckCode, fs.SiteCode AS FromSiteCode, ts.SiteCode AS ToSiteCode, d.Status,
    d.DepartedAtUtc, d.ArrivedAtUtc,
    CONVERT(int, CONVERT(char(8), CONVERT(date, (d.DepartedAtUtc AT TIME ZONE 'UTC') AT TIME ZONE fs.TimeZoneId), 112)) AS DepartedDateKey,
    DATEDIFF(MINUTE, d.DepartedAtUtc, d.ArrivedAtUtc) AS TransitMinutes,
    x.ItemCount,
    x.WeightTons
FROM logistics.Deliveries AS d
JOIN logistics.Trucks AS t ON t.TruckId = d.TruckId
JOIN dbo.Sites AS fs ON fs.SiteId = d.FromSiteId
JOIN dbo.Sites AS ts ON ts.SiteId = d.ToSiteId
CROSS APPLY (
    SELECT COUNT(*) AS ItemCount, CAST(ISNULL(SUM(i.WeightLbs), 0) / 2000.0 AS decimal(12,3)) AS WeightTons
    FROM logistics.DeliveryItems AS di
    JOIN dbo.Items AS i ON i.ItemId = di.ItemId
    WHERE di.DeliveryId = d.DeliveryId
) AS x;
GO

CREATE OR ALTER VIEW rpt.vw_GpsPings
AS
SELECT g.GpsPingId, g.DeliveryId, t.TruckCode, g.Latitude, g.Longitude, g.SpeedMph, g.HeadingDeg, g.RecordedAtUtc
FROM logistics.GpsPings AS g
JOIN logistics.Trucks AS t ON t.TruckId = g.TruckId;
GO

CREATE OR ALTER VIEW rpt.vw_ScanExceptions
AS
SELECT
    e.ScanExceptionId, e.ExceptionType, e.AttemptedAction, e.RawTag, e.OccurredAtUtc, e.LocationId,
    CONVERT(int, CONVERT(char(8), CONVERT(date, (e.OccurredAtUtc AT TIME ZONE 'UTC') AT TIME ZONE 'Central Standard Time'), 112)) AS DateKey,
    s.StationCode,
    CAST(CASE WHEN e.ResolvedAtUtc IS NULL THEN 0 ELSE 1 END AS bit) AS IsResolved
FROM dbo.ScanExceptions AS e
LEFT JOIN dbo.Stations AS s ON s.StationId = e.StationId;
GO

/* ----------------------------------------------------------------------------
   SSRS dataset procedures. @SiteCode = 'ALL' (or NULL) means every site.
   ---------------------------------------------------------------------------- */
CREATE OR ALTER PROCEDURE rpt.usp_SiteList
AS
BEGIN
    SET NOCOUNT ON;
    SELECT SiteCode, SiteName
    FROM (
        SELECT 'ALL' AS SiteCode, N'(All sites)' AS SiteName, 0 AS SortOrder
        UNION ALL
        SELECT SiteCode, Name, 1 FROM dbo.Sites WHERE IsActive = 1
    ) AS x
    ORDER BY SortOrder, SiteName;
END
GO

CREATE OR ALTER PROCEDURE rpt.usp_CurrentInventoryByLocation
    @SiteCode varchar(10) = 'ALL'
AS
BEGIN
    SET NOCOUNT ON;
    SELECT
        s.SiteCode,
        s.Name AS SiteName,
        l.LocationCode,
        l.Name AS LocationName,
        l.LocationType,
        l.CapacityTons,
        COUNT(i.ItemId)                                             AS ItemCount,
        ISNULL(SUM(i.Pieces), 0)                                    AS Pieces,
        CAST(ISNULL(SUM(i.WeightLbs), 0) / 2000.0 AS decimal(12,2)) AS WeightTons,
        CAST(CASE WHEN l.CapacityTons > 0
                  THEN ISNULL(SUM(i.WeightLbs), 0) / 2000.0 / l.CapacityTons END AS decimal(9,4)) AS Utilization,
        MAX(DATEDIFF(HOUR, i.LastMovedAtUtc, SYSUTCDATETIME()) / 24) AS OldestItemDays
    FROM dbo.Locations AS l
    JOIN dbo.Sites AS s ON s.SiteId = l.SiteId
    LEFT JOIN dbo.Items AS i ON i.CurrentLocationId = l.LocationId
    WHERE l.IsActive = 1
      AND (@SiteCode IS NULL OR @SiteCode = 'ALL' OR s.SiteCode = @SiteCode)
    GROUP BY s.SiteCode, s.Name, l.LocationCode, l.Name, l.LocationType, l.CapacityTons
    ORDER BY s.SiteCode, l.LocationCode;
END
GO

CREATE OR ALTER PROCEDURE rpt.usp_ItemsOverdueForMovement
    @DaysThreshold  int         = 30,
    @SiteCode       varchar(10) = 'ALL'
AS
BEGIN
    SET NOCOUNT ON;
    SELECT
        s.SiteCode,
        l.LocationCode,
        l.Name AS LocationName,
        i.TagId,
        p.ProductCode,
        p.Category,
        p.Description,
        i.HeatNumber,
        i.Pieces,
        CAST(i.WeightLbs / 2000.0 AS decimal(12,2)) AS WeightTons,
        CAST((i.LastMovedAtUtc AT TIME ZONE 'UTC') AT TIME ZONE s.TimeZoneId AS datetime2(0)) AS LastMovedLocal,
        a.DaysSinceLastMove,
        CASE WHEN a.DaysSinceLastMove >= 2 * @DaysThreshold THEN 'Critical' ELSE 'Overdue' END AS Severity
    FROM dbo.Items AS i
    JOIN dbo.Products AS p ON p.ProductId = i.ProductId
    JOIN dbo.Locations AS l ON l.LocationId = i.CurrentLocationId
    JOIN dbo.Sites AS s ON s.SiteId = l.SiteId
    CROSS APPLY (SELECT DATEDIFF(HOUR, i.LastMovedAtUtc, SYSUTCDATETIME()) / 24 AS DaysSinceLastMove) AS a
    WHERE i.Status = 'InYard'
      AND a.DaysSinceLastMove >= @DaysThreshold
      AND (@SiteCode IS NULL OR @SiteCode = 'ALL' OR s.SiteCode = @SiteCode)
    ORDER BY a.DaysSinceLastMove DESC, s.SiteCode, l.LocationCode;
END
GO

CREATE OR ALTER PROCEDURE rpt.usp_DailyThroughput
    @StartDate  date,
    @EndDate    date,
    @SiteCode   varchar(10) = 'ALL'
AS
BEGIN
    SET NOCOUNT ON;
    WITH movement AS (
        SELECT
            m.BusinessDate,
            SUM(m.Receipts)        AS Receipts,
            SUM(m.CheckIns)        AS CheckIns,
            SUM(m.CheckOuts)       AS CheckOuts,
            SUM(m.MovesIn)         AS Moves,
            SUM(m.TruckLoads)      AS TruckLoads,
            SUM(m.TruckDeliveries) AS TruckDeliveries,
            SUM(m.InboundTons)     AS InboundTons,
            SUM(m.OutboundTons)    AS OutboundTons
        FROM rpt.DailyInventoryMovement AS m
        JOIN dbo.Locations AS l ON l.LocationId = m.LocationId
        JOIN dbo.Sites AS s ON s.SiteId = l.SiteId
        WHERE m.BusinessDate BETWEEN @StartDate AND @EndDate
          AND (@SiteCode IS NULL OR @SiteCode = 'ALL' OR s.SiteCode = @SiteCode)
        GROUP BY m.BusinessDate
    )
    SELECT
        d.[Date] AS BusinessDate,
        d.DayName,
        d.IsWeekend,
        ISNULL(mv.Receipts, 0)        AS Receipts,
        ISNULL(mv.CheckIns, 0)        AS CheckIns,
        ISNULL(mv.CheckOuts, 0)       AS CheckOuts,
        ISNULL(mv.Moves, 0)           AS Moves,
        ISNULL(mv.TruckLoads, 0)      AS TruckLoads,
        ISNULL(mv.TruckDeliveries, 0) AS TruckDeliveries,
        ISNULL(mv.Receipts + mv.CheckIns + mv.CheckOuts + mv.Moves + mv.TruckLoads + mv.TruckDeliveries, 0) AS TotalScans,
        ISNULL(mv.InboundTons, 0)     AS InboundTons,
        ISNULL(mv.OutboundTons, 0)    AS OutboundTons
    FROM rpt.DimDate AS d
    LEFT JOIN movement AS mv ON mv.BusinessDate = d.[Date]
    WHERE d.[Date] BETWEEN @StartDate AND @EndDate
    ORDER BY d.[Date];
END
GO

/* Used by sharepoint/Publish-ScanExceptionReport.ps1 */
CREATE OR ALTER PROCEDURE rpt.usp_ScanExceptionReport
    @BusinessDate  date,
    @TimeZoneId    varchar(50) = 'Central Standard Time'
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @startUtc datetime2(3) = CAST((CAST(@BusinessDate AS datetime2(3)) AT TIME ZONE @TimeZoneId) AT TIME ZONE 'UTC' AS datetime2(3));
    DECLARE @endUtc   datetime2(3) = DATEADD(DAY, 1, @startUtc);

    SELECT
        e.ScanExceptionId,
        CAST((e.OccurredAtUtc AT TIME ZONE 'UTC') AT TIME ZONE @TimeZoneId AS datetime2(0)) AS OccurredLocal,
        e.ExceptionType,
        e.AttemptedAction,
        e.RawTag,
        e.Message,
        o.DisplayName AS OperatorName,
        st.StationCode,
        l.LocationCode,
        CASE WHEN e.ResolvedAtUtc IS NULL THEN 'Open' ELSE 'Resolved' END AS ResolutionStatus
    FROM dbo.ScanExceptions AS e
    LEFT JOIN dbo.Operators AS o ON o.OperatorId = e.OperatorId
    LEFT JOIN dbo.Stations AS st ON st.StationId = e.StationId
    LEFT JOIN dbo.Locations AS l ON l.LocationId = e.LocationId
    WHERE e.OccurredAtUtc >= @startUtc AND e.OccurredAtUtc < @endUtc
    ORDER BY e.OccurredAtUtc;
END
GO

CREATE OR ALTER PROCEDURE rpt.usp_MarkExceptionsPublished
    @ScanExceptionIds dbo.IdList READONLY
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE e
    SET PublishedAtUtc = SYSUTCDATETIME()
    FROM dbo.ScanExceptions AS e
    JOIN @ScanExceptionIds AS x ON x.Id = e.ScanExceptionId;
END
GO
