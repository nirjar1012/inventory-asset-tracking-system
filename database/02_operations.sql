/* =============================================================================
   YardTracker — 02_operations.sql
   Views and stored procedures called by the WCF service.

   Error contract
     * Business outcomes (a scan that is not allowed) are returned as a result
       row with Outcome = 'Rejected' and logged to dbo.ScanExceptions. They are
       expected on a shop floor and are not errors.
     * Caller mistakes (missing arguments, unknown badge at sign-in) are raised
       with THROW 50000-50999. The service maps these to a ServiceFault.
     * Anything else is an infrastructure failure.
   ============================================================================= */
SET XACT_ABORT ON;
GO

CREATE OR ALTER VIEW dbo.vw_ItemDetails
AS
SELECT
    i.ItemId,
    i.TagId,
    p.ProductCode,
    p.Category,
    p.Description,
    p.Grade,
    i.HeatNumber,
    i.Pieces,
    i.TotalLengthFt,
    i.WeightLbs,
    i.Status,
    l.LocationCode,
    l.Name              AS LocationName,
    s.SiteCode,
    i.LastMovedAtUtc,
    DATEDIFF(HOUR, i.LastMovedAtUtc, SYSUTCDATETIME()) / 24 AS DaysSinceLastMove
FROM dbo.Items AS i
JOIN dbo.Products AS p ON p.ProductId = i.ProductId
LEFT JOIN dbo.Locations AS l ON l.LocationId = i.CurrentLocationId
LEFT JOIN dbo.Sites AS s ON s.SiteId = l.SiteId;
GO

CREATE OR ALTER VIEW dbo.vw_ScanEventDetails
AS
SELECT
    se.ScanEventId,
    se.EventType,
    i.TagId,
    fl.LocationCode   AS FromLocationCode,
    tl.LocationCode   AS ToLocationCode,
    o.DisplayName     AS OperatorName,
    st.StationCode,
    se.DeliveryId,
    se.Reference,
    se.Pieces,
    se.WeightLbs,
    se.ScannedAtUtc,
    se.ReceivedAtUtc
FROM dbo.ScanEvents AS se
JOIN dbo.Items AS i ON i.ItemId = se.ItemId
JOIN dbo.Operators AS o ON o.OperatorId = se.OperatorId
LEFT JOIN dbo.Stations AS st ON st.StationId = se.StationId
LEFT JOIN dbo.Locations AS fl ON fl.LocationId = se.FromLocationId
LEFT JOIN dbo.Locations AS tl ON tl.LocationId = se.ToLocationId;
GO

/* ----------------------------------------------------------------------------
   Sessions and lookups
   ---------------------------------------------------------------------------- */
CREATE OR ALTER PROCEDURE dbo.usp_SignIn
    @BadgeNumber  varchar(20),
    @StationCode  varchar(20)
AS
BEGIN
    SET NOCOUNT ON;

    IF NOT EXISTS (SELECT 1 FROM dbo.Operators WHERE BadgeNumber = @BadgeNumber AND IsActive = 1)
        THROW 50010, 'Badge is not recognised or the operator is inactive.', 1;

    IF NOT EXISTS (SELECT 1 FROM dbo.Stations WHERE StationCode = @StationCode AND IsActive = 1)
        THROW 50011, 'This station is not registered. Ask a supervisor to add it.', 1;

    UPDATE dbo.Stations SET LastSeenAtUtc = SYSUTCDATETIME() WHERE StationCode = @StationCode;

    SELECT
        o.BadgeNumber,
        o.DisplayName,
        o.Role,
        s.StationCode,
        s.Name          AS StationName,
        s.DeviceType,
        l.LocationCode  AS DefaultLocationCode,
        SYSUTCDATETIME() AS ServerTimeUtc
    FROM dbo.Operators AS o
    CROSS JOIN dbo.Stations AS s
    LEFT JOIN dbo.Locations AS l ON l.LocationId = s.DefaultLocationId
    WHERE o.BadgeNumber = @BadgeNumber
      AND s.StationCode = @StationCode;
END
GO

CREATE OR ALTER PROCEDURE dbo.usp_GetSites
AS
BEGIN
    SET NOCOUNT ON;
    SELECT SiteCode, Name, Latitude, Longitude
    FROM dbo.Sites
    WHERE IsActive = 1
    ORDER BY SiteCode;
END
GO

CREATE OR ALTER PROCEDURE dbo.usp_GetLocations
    @SiteCode varchar(10) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SELECT l.LocationCode, l.Name, l.LocationType, s.SiteCode, s.Name AS SiteName, l.CapacityTons
    FROM dbo.Locations AS l
    JOIN dbo.Sites AS s ON s.SiteId = l.SiteId
    WHERE l.IsActive = 1
      AND (@SiteCode IS NULL OR s.SiteCode = @SiteCode)
    ORDER BY s.SiteCode, l.LocationCode;
END
GO

CREATE OR ALTER PROCEDURE dbo.usp_GetProducts
AS
BEGIN
    SET NOCOUNT ON;
    SELECT ProductCode, Category, Description, Grade, OuterDiameterIn, WallThicknessIn, NominalLengthFt, WeightLbsPerFt
    FROM dbo.Products
    ORDER BY Category, ProductCode;
END
GO

CREATE OR ALTER PROCEDURE dbo.usp_GetItemByTag
    @TagId varchar(64)
AS
BEGIN
    SET NOCOUNT ON;
    SELECT d.*
    FROM dbo.vw_ItemDetails AS d
    WHERE d.TagId = UPPER(LTRIM(RTRIM(@TagId)));
END
GO

CREATE OR ALTER PROCEDURE dbo.usp_SearchItems
    @Status        varchar(12) = NULL,
    @LocationCode  varchar(20) = NULL,
    @SiteCode      varchar(10) = NULL,
    @Top           int         = 100
AS
BEGIN
    SET NOCOUNT ON;
    SELECT TOP (@Top) d.*
    FROM dbo.vw_ItemDetails AS d
    WHERE (@Status IS NULL OR d.Status = @Status)
      AND (@LocationCode IS NULL OR d.LocationCode = @LocationCode)
      AND (@SiteCode IS NULL OR d.SiteCode = @SiteCode)
    ORDER BY d.LastMovedAtUtc DESC;
END
GO

CREATE OR ALTER PROCEDURE dbo.usp_GetItemHistory
    @TagId  varchar(64),
    @Top    int = 200
AS
BEGIN
    SET NOCOUNT ON;
    SELECT TOP (@Top) h.*
    FROM dbo.vw_ScanEventDetails AS h
    WHERE h.TagId = UPPER(LTRIM(RTRIM(@TagId)))
    ORDER BY h.ScannedAtUtc DESC, h.ScanEventId DESC;
END
GO

CREATE OR ALTER PROCEDURE dbo.usp_GetRecentScans
    @StationCode  varchar(20) = NULL,
    @Top          int         = 50
AS
BEGIN
    SET NOCOUNT ON;
    SELECT TOP (@Top) h.*
    FROM dbo.vw_ScanEventDetails AS h
    WHERE @StationCode IS NULL OR h.StationCode = @StationCode
    ORDER BY h.ScanEventId DESC;
END
GO

CREATE OR ALTER PROCEDURE dbo.usp_GetInventoryByLocation
    @SiteCode varchar(10) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SELECT
        s.SiteCode,
        l.LocationCode,
        l.Name AS LocationName,
        l.LocationType,
        l.CapacityTons,
        COUNT(i.ItemId)                                              AS ItemCount,
        ISNULL(SUM(i.Pieces), 0)                                     AS Pieces,
        CAST(ISNULL(SUM(i.WeightLbs), 0) / 2000.0 AS decimal(12,2))  AS WeightTons,
        CAST(CASE WHEN l.CapacityTons > 0
                  THEN ISNULL(SUM(i.WeightLbs), 0) / 2000.0 / l.CapacityTons * 100 END AS decimal(6,1)) AS UtilizationPct,
        MIN(i.LastMovedAtUtc)                                        AS OldestMoveAtUtc
    FROM dbo.Locations AS l
    JOIN dbo.Sites AS s ON s.SiteId = l.SiteId
    LEFT JOIN dbo.Items AS i ON i.CurrentLocationId = l.LocationId
    WHERE l.IsActive = 1
      AND (@SiteCode IS NULL OR s.SiteCode = @SiteCode)
    GROUP BY s.SiteCode, l.LocationCode, l.Name, l.LocationType, l.CapacityTons
    ORDER BY s.SiteCode, l.LocationCode;
END
GO

/* ----------------------------------------------------------------------------
   Scans
   ---------------------------------------------------------------------------- */
CREATE OR ALTER PROCEDURE dbo.usp_LogScanException
    @ClientScanId     uniqueidentifier,
    @RawTag           varchar(64),
    @AttemptedAction  varchar(12),
    @ExceptionType    varchar(30),
    @Message          nvarchar(400),
    @ItemId           int = NULL,
    @OperatorId       int = NULL,
    @StationId        int = NULL,
    @LocationId       int = NULL
AS
BEGIN
    SET NOCOUNT ON;
    BEGIN TRY
        INSERT dbo.ScanExceptions (ClientScanId, RawTag, AttemptedAction, ExceptionType, Message, ItemId, OperatorId, StationId, LocationId)
        SELECT @ClientScanId, ISNULL(@RawTag, ''), @AttemptedAction, @ExceptionType, @Message, @ItemId, @OperatorId, @StationId, @LocationId
        WHERE @ClientScanId IS NULL
           OR NOT EXISTS (SELECT 1 FROM dbo.ScanExceptions WHERE ClientScanId = @ClientScanId);
    END TRY
    BEGIN CATCH
        -- A concurrent retry already logged this exception.
        IF ERROR_NUMBER() NOT IN (2601, 2627) THROW;
    END CATCH
END
GO

/* Core scan state machine shared by CheckIn / CheckOut / Move.

     CheckIn   CheckedOut -> InYard  (item returns from a job / customer)
     CheckOut  InYard     -> CheckedOut
     Move      InYard     -> InYard  (different location, same site)

   Truck loading/unloading goes through logistics.usp_StartDelivery / usp_CompleteDelivery. */
CREATE OR ALTER PROCEDURE dbo.usp_RecordScan
    @Action         varchar(12),
    @TagId          varchar(64),
    @LocationCode   varchar(20)      = NULL,
    @OperatorBadge  varchar(20),
    @StationCode    varchar(20),
    @ClientScanId   uniqueidentifier,
    @ScannedAtUtc   datetime2(3)     = NULL,
    @Reference      nvarchar(50)     = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @Action IS NULL OR @Action NOT IN ('CheckIn', 'CheckOut', 'Move')
        THROW 50001, 'Action must be CheckIn, CheckOut or Move.', 1;
    IF @ClientScanId IS NULL
        THROW 50002, 'ClientScanId is required.', 1;
    IF NULLIF(LTRIM(RTRIM(@TagId)), '') IS NULL
        THROW 50003, 'TagId is required.', 1;

    DECLARE
        @now                  datetime2(3) = SYSUTCDATETIME(),
        @tag                  varchar(64)  = UPPER(LTRIM(RTRIM(@TagId))),
        @outcome              varchar(10),
        @exceptionType        varchar(30),
        @message              nvarchar(400),
        @scanEventId          bigint,
        @itemId               int,
        @operatorId           int,
        @stationId            int,
        @toLocationId         int,
        @toSiteId             int,
        @fromLocationId       int,
        @fromSiteId           int,
        @status               varchar(12),
        @currentLocationCode  varchar(20);

    SET @ScannedAtUtc = ISNULL(@ScannedAtUtc, @now);

    /* 1. Idempotency: a handheld retrying after a dropped connection gets the original answer. */
    SELECT @scanEventId = ScanEventId, @itemId = ItemId
    FROM dbo.ScanEvents
    WHERE ClientScanId = @ClientScanId;

    IF @scanEventId IS NOT NULL
        SELECT @outcome = 'Duplicate', @message = N'Scan was already recorded; retry ignored.';
    ELSE
        SELECT @outcome = 'Rejected', @exceptionType = ExceptionType, @message = Message, @itemId = ItemId
        FROM dbo.ScanExceptions
        WHERE ClientScanId = @ClientScanId;

    /* 2. Resolve who and where. */
    IF @outcome IS NULL
    BEGIN
        SELECT @operatorId = OperatorId FROM dbo.Operators WHERE BadgeNumber = @OperatorBadge AND IsActive = 1;
        SELECT @stationId = StationId FROM dbo.Stations WHERE StationCode = @StationCode AND IsActive = 1;

        IF @Action IN ('CheckIn', 'Move')
            SELECT @toLocationId = LocationId, @toSiteId = SiteId
            FROM dbo.Locations
            WHERE LocationCode = @LocationCode AND IsActive = 1;

        IF @operatorId IS NULL
            SELECT @exceptionType = 'UnknownOperator',
                   @message = CONCAT(N'Badge ''', @OperatorBadge, N''' is not an active operator.');
        ELSE IF @stationId IS NULL
            SELECT @exceptionType = 'UnknownStation',
                   @message = CONCAT(N'Station ''', @StationCode, N''' is not registered.');
        ELSE IF @ScannedAtUtc > DATEADD(MINUTE, 5, @now)
            SELECT @exceptionType = 'ClockSkew',
                   @message = N'Device clock is more than 5 minutes ahead of the server. Sync the handheld clock.';
        ELSE IF @Action IN ('CheckIn', 'Move') AND @toLocationId IS NULL
            SELECT @exceptionType = 'UnknownLocation',
                   @message = CONCAT(N'Location ''', ISNULL(@LocationCode, N'(none)'), N''' is not an active location.');
    END

    /* 3. Apply the state transition while holding an update lock on the item row. */
    IF @outcome IS NULL AND @exceptionType IS NULL
    BEGIN
        BEGIN TRY
            BEGIN TRANSACTION;

            SELECT @itemId = i.ItemId,
                   @status = i.Status,
                   @fromLocationId = i.CurrentLocationId,
                   @fromSiteId = l.SiteId,
                   @currentLocationCode = l.LocationCode
            FROM dbo.Items AS i WITH (UPDLOCK, HOLDLOCK)
            LEFT JOIN dbo.Locations AS l ON l.LocationId = i.CurrentLocationId
            WHERE i.TagId = @tag;

            IF @itemId IS NULL
                SELECT @exceptionType = 'UnknownTag',
                       @message = CONCAT(N'Tag ''', @tag, N''' is not registered. Receive the item first.');
            ELSE IF @Action = 'CheckIn' AND @status = 'InYard'
                SELECT @exceptionType = 'InvalidState',
                       @message = CONCAT(N'Item is already in the yard at ', @currentLocationCode, N'. Use Move to relocate it.');
            ELSE IF @Action = 'CheckIn' AND @status = 'InTransit'
                SELECT @exceptionType = 'InvalidState',
                       @message = N'Item is on a truck delivery. Complete the delivery at the destination site.';
            ELSE IF @Action IN ('CheckOut', 'Move') AND @status <> 'InYard'
                SELECT @exceptionType = 'InvalidState',
                       @message = CONCAT(N'Item is not in the yard (status: ', @status, N').');
            ELSE IF @Action = 'Move' AND @fromLocationId = @toLocationId
                SELECT @exceptionType = 'InvalidState',
                       @message = CONCAT(N'Item is already at ', @currentLocationCode, N'.');
            ELSE IF @Action = 'Move' AND @fromSiteId <> @toSiteId
                SELECT @exceptionType = 'WrongSite',
                       @message = N'Moves between sites must go on a truck delivery.';

            IF @exceptionType IS NULL
            BEGIN
                UPDATE dbo.Items
                SET Status            = CASE @Action WHEN 'CheckOut' THEN 'CheckedOut' ELSE 'InYard' END,
                    CurrentLocationId = CASE @Action WHEN 'CheckOut' THEN NULL ELSE @toLocationId END,
                    LastMovedAtUtc    = CASE WHEN @ScannedAtUtc > LastMovedAtUtc THEN @ScannedAtUtc ELSE LastMovedAtUtc END
                WHERE ItemId = @itemId;

                INSERT dbo.ScanEvents
                    (ClientScanId, ItemId, EventType, FromLocationId, ToLocationId, OperatorId, StationId, Reference, Pieces, WeightLbs, ScannedAtUtc)
                SELECT @ClientScanId, ItemId, @Action, @fromLocationId,
                       CASE WHEN @Action = 'CheckOut' THEN NULL ELSE @toLocationId END,
                       @operatorId, @stationId, @Reference, Pieces, WeightLbs, @ScannedAtUtc
                FROM dbo.Items
                WHERE ItemId = @itemId;

                SET @scanEventId = SCOPE_IDENTITY();
                SET @outcome = 'Accepted';
                SET @message = CASE @Action
                    WHEN 'CheckIn'  THEN CONCAT(N'Checked in to ', @LocationCode, N'.')
                    WHEN 'CheckOut' THEN CONCAT(N'Checked out from ', @currentLocationCode, N'.')
                    ELSE CONCAT(N'Moved ', @currentLocationCode, N' -> ', @LocationCode, N'.')
                END;
            END

            COMMIT TRANSACTION;
        END TRY
        BEGIN CATCH
            IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;

            IF ERROR_NUMBER() IN (2601, 2627) AND EXISTS (SELECT 1 FROM dbo.ScanEvents WHERE ClientScanId = @ClientScanId)
                SELECT @outcome = 'Duplicate', @exceptionType = NULL,
                       @message = N'Scan was already recorded; retry ignored.',
                       @scanEventId = (SELECT ScanEventId FROM dbo.ScanEvents WHERE ClientScanId = @ClientScanId);
            ELSE
                THROW;
        END CATCH
    END

    /* 4. Rejections are logged outside the transaction for supervisor review. */
    IF @outcome IS NULL AND @exceptionType IS NOT NULL
    BEGIN
        EXEC dbo.usp_LogScanException @ClientScanId, @tag, @Action, @exceptionType, @message, @itemId, @operatorId, @stationId, @toLocationId;
        SET @outcome = 'Rejected';
    END

    IF @stationId IS NOT NULL
        UPDATE dbo.Stations SET LastSeenAtUtc = @now WHERE StationId = @stationId;

    SELECT @outcome AS Outcome, @exceptionType AS ExceptionType, @message AS Message,
           @scanEventId AS ScanEventId, @tag AS ScannedTag, d.*
    FROM (VALUES (1)) AS o (x)
    LEFT JOIN dbo.vw_ItemDetails AS d ON d.ItemId = @itemId;
END
GO

CREATE OR ALTER PROCEDURE dbo.usp_CheckInItem
    @TagId varchar(64), @LocationCode varchar(20), @OperatorBadge varchar(20), @StationCode varchar(20),
    @ClientScanId uniqueidentifier, @ScannedAtUtc datetime2(3) = NULL, @Reference nvarchar(50) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    EXEC dbo.usp_RecordScan 'CheckIn', @TagId, @LocationCode, @OperatorBadge, @StationCode, @ClientScanId, @ScannedAtUtc, @Reference;
END
GO

CREATE OR ALTER PROCEDURE dbo.usp_CheckOutItem
    @TagId varchar(64), @OperatorBadge varchar(20), @StationCode varchar(20),
    @ClientScanId uniqueidentifier, @ScannedAtUtc datetime2(3) = NULL, @Reference nvarchar(50) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    EXEC dbo.usp_RecordScan 'CheckOut', @TagId, NULL, @OperatorBadge, @StationCode, @ClientScanId, @ScannedAtUtc, @Reference;
END
GO

CREATE OR ALTER PROCEDURE dbo.usp_MoveItem
    @TagId varchar(64), @LocationCode varchar(20), @OperatorBadge varchar(20), @StationCode varchar(20),
    @ClientScanId uniqueidentifier, @ScannedAtUtc datetime2(3) = NULL, @Reference nvarchar(50) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    EXEC dbo.usp_RecordScan 'Move', @TagId, @LocationCode, @OperatorBadge, @StationCode, @ClientScanId, @ScannedAtUtc, @Reference;
END
GO

/* Receiving: registers a newly tagged item (e.g. a bundle off a mill shipment) at a yard location. */
CREATE OR ALTER PROCEDURE dbo.usp_ReceiveItem
    @TagId          varchar(64),
    @ProductCode    varchar(30),
    @HeatNumber     varchar(20),
    @Pieces         int,
    @TotalLengthFt  decimal(9,2)     = NULL,
    @LocationCode   varchar(20),
    @OperatorBadge  varchar(20),
    @StationCode    varchar(20),
    @ClientScanId   uniqueidentifier,
    @ScannedAtUtc   datetime2(3)     = NULL,
    @Reference      nvarchar(50)     = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @tag varchar(64) = UPPER(LTRIM(RTRIM(@TagId)));

    IF @ClientScanId IS NULL           THROW 50002, 'ClientScanId is required.', 1;
    IF ISNULL(LEN(@tag), 0) = 0        THROW 50003, 'TagId is required.', 1;
    IF LEN(@tag) > 32                  THROW 50004, 'TagId cannot be longer than 32 characters.', 1;
    IF ISNULL(@Pieces, 0) <= 0         THROW 50005, 'Pieces must be greater than zero.', 1;
    IF NULLIF(LTRIM(RTRIM(@HeatNumber)), '') IS NULL
                                       THROW 50006, 'HeatNumber is required for traceability.', 1;
    IF @TotalLengthFt IS NOT NULL AND @TotalLengthFt <= 0
                                       THROW 50007, 'TotalLengthFt must be greater than zero.', 1;

    DECLARE
        @now            datetime2(3) = SYSUTCDATETIME(),
        @outcome        varchar(10),
        @exceptionType  varchar(30),
        @message        nvarchar(400),
        @scanEventId    bigint,
        @itemId         int,
        @operatorId     int,
        @stationId      int,
        @toLocationId   int,
        @productId      int,
        @nominalLength  decimal(7,2),
        @weightPerFt    decimal(9,3);

    SET @ScannedAtUtc = ISNULL(@ScannedAtUtc, @now);

    SELECT @scanEventId = ScanEventId, @itemId = ItemId FROM dbo.ScanEvents WHERE ClientScanId = @ClientScanId;
    IF @scanEventId IS NOT NULL
        SELECT @outcome = 'Duplicate', @message = N'Receipt was already recorded; retry ignored.';
    ELSE
        SELECT @outcome = 'Rejected', @exceptionType = ExceptionType, @message = Message, @itemId = ItemId
        FROM dbo.ScanExceptions WHERE ClientScanId = @ClientScanId;

    IF @outcome IS NULL
    BEGIN
        SELECT @operatorId = OperatorId FROM dbo.Operators WHERE BadgeNumber = @OperatorBadge AND IsActive = 1;
        SELECT @stationId = StationId FROM dbo.Stations WHERE StationCode = @StationCode AND IsActive = 1;
        SELECT @toLocationId = LocationId FROM dbo.Locations WHERE LocationCode = @LocationCode AND IsActive = 1;
        SELECT @productId = ProductId, @nominalLength = NominalLengthFt, @weightPerFt = WeightLbsPerFt
        FROM dbo.Products WHERE ProductCode = @ProductCode;

        IF @operatorId IS NULL
            SELECT @exceptionType = 'UnknownOperator', @message = CONCAT(N'Badge ''', @OperatorBadge, N''' is not an active operator.');
        ELSE IF @stationId IS NULL
            SELECT @exceptionType = 'UnknownStation', @message = CONCAT(N'Station ''', @StationCode, N''' is not registered.');
        ELSE IF @ScannedAtUtc > DATEADD(MINUTE, 5, @now)
            SELECT @exceptionType = 'ClockSkew', @message = N'Device clock is more than 5 minutes ahead of the server. Sync the handheld clock.';
        ELSE IF @toLocationId IS NULL
            SELECT @exceptionType = 'UnknownLocation', @message = CONCAT(N'Location ''', ISNULL(@LocationCode, N'(none)'), N''' is not an active location.');
        ELSE IF @productId IS NULL
            SELECT @exceptionType = 'UnknownProduct', @message = CONCAT(N'Product ''', @ProductCode, N''' is not in the catalog.');
    END

    IF @outcome IS NULL AND @exceptionType IS NULL
    BEGIN
        BEGIN TRY
            BEGIN TRANSACTION;

            SELECT @itemId = ItemId FROM dbo.Items WITH (UPDLOCK, HOLDLOCK) WHERE TagId = @tag;

            IF @itemId IS NOT NULL
                SELECT @exceptionType = 'DuplicateTag',
                       @message = CONCAT(N'Tag ''', @tag, N''' is already assigned to another item. Re-tag the bundle.');
            ELSE
            BEGIN
                DECLARE @length decimal(9,2) = ISNULL(@TotalLengthFt, @Pieces * @nominalLength);

                INSERT dbo.Items (TagId, ProductId, HeatNumber, Pieces, TotalLengthFt, WeightLbs, Status, CurrentLocationId, LastMovedAtUtc)
                VALUES (@tag, @productId, UPPER(LTRIM(RTRIM(@HeatNumber))), @Pieces, @length,
                        ROUND(@length * @weightPerFt, 2), 'InYard', @toLocationId, @ScannedAtUtc);

                SET @itemId = SCOPE_IDENTITY();

                INSERT dbo.ScanEvents
                    (ClientScanId, ItemId, EventType, FromLocationId, ToLocationId, OperatorId, StationId, Reference, Pieces, WeightLbs, ScannedAtUtc)
                SELECT @ClientScanId, ItemId, 'Receive', NULL, @toLocationId, @operatorId, @stationId, @Reference, Pieces, WeightLbs, @ScannedAtUtc
                FROM dbo.Items WHERE ItemId = @itemId;

                SELECT @scanEventId = SCOPE_IDENTITY(), @outcome = 'Accepted',
                       @message = CONCAT(N'Received into ', @LocationCode, N'.');
            END

            COMMIT TRANSACTION;
        END TRY
        BEGIN CATCH
            IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;

            IF ERROR_NUMBER() IN (2601, 2627) AND EXISTS (SELECT 1 FROM dbo.ScanEvents WHERE ClientScanId = @ClientScanId)
                SELECT @outcome = 'Duplicate', @exceptionType = NULL,
                       @message = N'Receipt was already recorded; retry ignored.',
                       @itemId = (SELECT ItemId FROM dbo.ScanEvents WHERE ClientScanId = @ClientScanId);
            ELSE IF ERROR_NUMBER() IN (2601, 2627) AND EXISTS (SELECT 1 FROM dbo.Items WHERE TagId = @tag)
                SELECT @exceptionType = 'DuplicateTag',
                       @message = CONCAT(N'Tag ''', @tag, N''' is already assigned to another item. Re-tag the bundle.'),
                       @itemId = (SELECT ItemId FROM dbo.Items WHERE TagId = @tag);
            ELSE
                THROW;
        END CATCH
    END

    IF @outcome IS NULL AND @exceptionType IS NOT NULL
    BEGIN
        EXEC dbo.usp_LogScanException @ClientScanId, @tag, 'Receive', @exceptionType, @message, @itemId, @operatorId, @stationId, @toLocationId;
        SET @outcome = 'Rejected';
    END

    IF @stationId IS NOT NULL
        UPDATE dbo.Stations SET LastSeenAtUtc = @now WHERE StationId = @stationId;

    SELECT @outcome AS Outcome, @exceptionType AS ExceptionType, @message AS Message,
           @scanEventId AS ScanEventId, @tag AS ScannedTag, d.*
    FROM (VALUES (1)) AS o (x)
    LEFT JOIN dbo.vw_ItemDetails AS d ON d.ItemId = @itemId;
END
GO

CREATE OR ALTER PROCEDURE dbo.usp_GetScanExceptions
    @SinceUtc        datetime2(3) = NULL,
    @UnresolvedOnly  bit          = 1,
    @Top             int          = 200
AS
BEGIN
    SET NOCOUNT ON;
    SELECT TOP (@Top)
        e.ScanExceptionId,
        e.OccurredAtUtc,
        e.ExceptionType,
        e.AttemptedAction,
        e.RawTag,
        e.Message,
        o.DisplayName AS OperatorName,
        s.StationCode,
        l.LocationCode,
        e.ResolvedAtUtc,
        e.ResolvedBy
    FROM dbo.ScanExceptions AS e
    LEFT JOIN dbo.Operators AS o ON o.OperatorId = e.OperatorId
    LEFT JOIN dbo.Stations AS s ON s.StationId = e.StationId
    LEFT JOIN dbo.Locations AS l ON l.LocationId = e.LocationId
    WHERE (@SinceUtc IS NULL OR e.OccurredAtUtc >= @SinceUtc)
      AND (@UnresolvedOnly = 0 OR e.ResolvedAtUtc IS NULL)
    ORDER BY e.OccurredAtUtc DESC;
END
GO

/* ----------------------------------------------------------------------------
   Logistics: truck deliveries between sites
   ---------------------------------------------------------------------------- */
CREATE OR ALTER PROCEDURE logistics.usp_StartDelivery
    @ClientRequestId  uniqueidentifier,
    @TruckCode        varchar(20),
    @DriverBadge      varchar(20),
    @FromSiteCode     varchar(10),
    @ToSiteCode       varchar(10),
    @TagIds           dbo.TagList READONLY,
    @StationCode      varchar(20)  = NULL,
    @DepartedAtUtc    datetime2(3) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @ClientRequestId IS NULL THROW 50002, 'ClientRequestId is required.', 1;

    DECLARE
        @now         datetime2(3) = SYSUTCDATETIME(),
        @outcome     varchar(10),
        @message     nvarchar(400),
        @deliveryId  int,
        @truckId     int,
        @maxPayload  decimal(10,0),
        @driverId    int,
        @fromSiteId  int,
        @toSiteId    int,
        @stationId   int,
        @problems    nvarchar(max);

    SET @DepartedAtUtc = ISNULL(@DepartedAtUtc, @now);

    SELECT @deliveryId = DeliveryId FROM logistics.Deliveries WHERE ClientRequestId = @ClientRequestId;
    IF @deliveryId IS NOT NULL
        SELECT @outcome = 'Duplicate', @message = N'Delivery was already started; retry ignored.';

    IF @outcome IS NULL
    BEGIN
        SELECT @truckId = TruckId, @maxPayload = MaxPayloadLbs FROM logistics.Trucks WHERE TruckCode = @TruckCode AND IsActive = 1;
        SELECT @driverId = OperatorId FROM dbo.Operators WHERE BadgeNumber = @DriverBadge AND IsActive = 1;
        SELECT @fromSiteId = SiteId FROM dbo.Sites WHERE SiteCode = @FromSiteCode AND IsActive = 1;
        SELECT @toSiteId = SiteId FROM dbo.Sites WHERE SiteCode = @ToSiteCode AND IsActive = 1;
        SELECT @stationId = StationId FROM dbo.Stations WHERE StationCode = @StationCode AND IsActive = 1;

        IF @truckId IS NULL
            SELECT @outcome = 'Rejected', @message = CONCAT(N'Truck ''', @TruckCode, N''' is not an active truck.');
        ELSE IF @driverId IS NULL
            SELECT @outcome = 'Rejected', @message = CONCAT(N'Driver badge ''', @DriverBadge, N''' is not active.');
        ELSE IF @fromSiteId IS NULL OR @toSiteId IS NULL
            SELECT @outcome = 'Rejected', @message = N'Origin or destination site is not valid.';
        ELSE IF @fromSiteId = @toSiteId
            SELECT @outcome = 'Rejected', @message = N'Origin and destination sites must be different.';
        ELSE IF NOT EXISTS (SELECT 1 FROM @TagIds)
            SELECT @outcome = 'Rejected', @message = N'Scan at least one item onto the truck.';
    END

    IF @outcome IS NULL
    BEGIN
        BEGIN TRY
            BEGIN TRANSACTION;

            DECLARE @items TABLE
            (
                ItemId      int PRIMARY KEY,
                TagId       varchar(32),
                Status      varchar(12),
                LocationId  int NULL,
                SiteId      int NULL,
                Pieces      int,
                WeightLbs   decimal(12,2)
            );

            INSERT @items (ItemId, TagId, Status, LocationId, SiteId, Pieces, WeightLbs)
            SELECT i.ItemId, i.TagId, i.Status, i.CurrentLocationId, l.SiteId, i.Pieces, i.WeightLbs
            FROM @TagIds AS t
            JOIN dbo.Items AS i WITH (UPDLOCK, HOLDLOCK) ON i.TagId = t.TagId
            LEFT JOIN dbo.Locations AS l ON l.LocationId = i.CurrentLocationId;

            SELECT @problems = STRING_AGG(p.Problem, N'; ')
            FROM (
                SELECT CAST(CONCAT(t.TagId, N' is not registered') AS nvarchar(200)) AS Problem
                FROM @TagIds AS t
                WHERE NOT EXISTS (SELECT 1 FROM @items AS x WHERE x.TagId = t.TagId)
                UNION ALL
                SELECT CONCAT(TagId, N' is ', Status) FROM @items WHERE Status <> 'InYard'
                UNION ALL
                SELECT CONCAT(TagId, N' is not at ', @FromSiteCode) FROM @items WHERE Status = 'InYard' AND SiteId <> @fromSiteId
            ) AS p;

            IF @problems IS NULL AND EXISTS (SELECT 1 FROM logistics.Deliveries WHERE TruckId = @truckId AND Status = 'InTransit')
                SET @problems = CONCAT(N'Truck ', @TruckCode, N' is already on an active delivery.');

            IF @problems IS NULL AND (SELECT SUM(WeightLbs) FROM @items) > @maxPayload
                SET @problems = CONCAT(N'Load of ', (SELECT CAST(SUM(WeightLbs) AS int) FROM @items),
                                       N' lbs exceeds the truck payload of ', CAST(@maxPayload AS int), N' lbs.');

            IF @problems IS NULL
            BEGIN
                INSERT logistics.Deliveries (ClientRequestId, TruckId, DriverOperatorId, FromSiteId, ToSiteId, Status, DepartedAtUtc)
                VALUES (@ClientRequestId, @truckId, @driverId, @fromSiteId, @toSiteId, 'InTransit', @DepartedAtUtc);

                SET @deliveryId = SCOPE_IDENTITY();

                INSERT logistics.DeliveryItems (DeliveryId, ItemId)
                SELECT @deliveryId, ItemId FROM @items;

                UPDATE i
                SET Status = 'InTransit',
                    CurrentLocationId = NULL,
                    LastMovedAtUtc = CASE WHEN @DepartedAtUtc > i.LastMovedAtUtc THEN @DepartedAtUtc ELSE i.LastMovedAtUtc END
                FROM dbo.Items AS i
                JOIN @items AS x ON x.ItemId = i.ItemId;

                INSERT dbo.ScanEvents
                    (ClientScanId, ItemId, EventType, FromLocationId, ToLocationId, OperatorId, StationId, DeliveryId, Reference, Pieces, WeightLbs, ScannedAtUtc)
                SELECT NEWID(), ItemId, 'LoadTruck', LocationId, NULL, @driverId, @stationId, @deliveryId, @TruckCode, Pieces, WeightLbs, @DepartedAtUtc
                FROM @items;

                SELECT @outcome = 'Accepted',
                       @message = CONCAT(N'Delivery #', @deliveryId, N' departed ', @FromSiteCode, N' for ', @ToSiteCode, N'.');
            END
            ELSE
                SELECT @outcome = 'Rejected', @message = LEFT(@problems, 400);

            COMMIT TRANSACTION;
        END TRY
        BEGIN CATCH
            IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;

            IF ERROR_NUMBER() IN (2601, 2627) AND EXISTS (SELECT 1 FROM logistics.Deliveries WHERE ClientRequestId = @ClientRequestId)
                SELECT @outcome = 'Duplicate', @message = N'Delivery was already started; retry ignored.',
                       @deliveryId = (SELECT DeliveryId FROM logistics.Deliveries WHERE ClientRequestId = @ClientRequestId);
            ELSE IF ERROR_NUMBER() IN (2601, 2627)
                SELECT @outcome = 'Rejected', @message = CONCAT(N'Truck ', @TruckCode, N' is already on an active delivery.');
            ELSE
                THROW;
        END CATCH
    END

    SELECT @outcome AS Outcome, @message AS Message, d.DeliveryId,
           (SELECT COUNT(*) FROM logistics.DeliveryItems AS di WHERE di.DeliveryId = d.DeliveryId) AS ItemCount,
           (SELECT ISNULL(SUM(i.WeightLbs), 0) FROM logistics.DeliveryItems AS di JOIN dbo.Items AS i ON i.ItemId = di.ItemId
             WHERE di.DeliveryId = d.DeliveryId) AS WeightLbs
    FROM (VALUES (1)) AS o (x)
    LEFT JOIN logistics.Deliveries AS d ON d.DeliveryId = @deliveryId;
END
GO

CREATE OR ALTER PROCEDURE logistics.usp_CompleteDelivery
    @DeliveryId           int,
    @ArrivalLocationCode  varchar(20),
    @OperatorBadge        varchar(20),
    @StationCode          varchar(20)  = NULL,
    @ArrivedAtUtc         datetime2(3) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE
        @now         datetime2(3) = SYSUTCDATETIME(),
        @outcome     varchar(10),
        @message     nvarchar(400),
        @status      varchar(12),
        @toSiteId    int,
        @operatorId  int,
        @stationId   int,
        @locationId  int,
        @locSiteId   int,
        @truckCode   varchar(20);

    SET @ArrivedAtUtc = ISNULL(@ArrivedAtUtc, @now);

    SELECT @operatorId = OperatorId FROM dbo.Operators WHERE BadgeNumber = @OperatorBadge AND IsActive = 1;
    SELECT @stationId = StationId FROM dbo.Stations WHERE StationCode = @StationCode AND IsActive = 1;
    SELECT @locationId = LocationId, @locSiteId = SiteId FROM dbo.Locations WHERE LocationCode = @ArrivalLocationCode AND IsActive = 1;

    BEGIN TRY
        BEGIN TRANSACTION;

        SELECT @status = d.Status, @toSiteId = d.ToSiteId, @truckCode = t.TruckCode
        FROM logistics.Deliveries AS d WITH (UPDLOCK, HOLDLOCK)
        JOIN logistics.Trucks AS t ON t.TruckId = d.TruckId
        WHERE d.DeliveryId = @DeliveryId;

        IF @status IS NULL
            SELECT @outcome = 'Rejected', @message = CONCAT(N'Delivery #', @DeliveryId, N' does not exist.');
        ELSE IF @status = 'Delivered'
            SELECT @outcome = 'Duplicate', @message = CONCAT(N'Delivery #', @DeliveryId, N' was already completed.');
        ELSE IF @operatorId IS NULL
            SELECT @outcome = 'Rejected', @message = CONCAT(N'Badge ''', @OperatorBadge, N''' is not an active operator.');
        ELSE IF @locationId IS NULL
            SELECT @outcome = 'Rejected', @message = CONCAT(N'Location ''', @ArrivalLocationCode, N''' is not an active location.');
        ELSE IF @locSiteId <> @toSiteId
            SELECT @outcome = 'Rejected', @message = N'Arrival location is not at the delivery''s destination site.';
        ELSE
        BEGIN
            INSERT dbo.ScanEvents
                (ClientScanId, ItemId, EventType, FromLocationId, ToLocationId, OperatorId, StationId, DeliveryId, Reference, Pieces, WeightLbs, ScannedAtUtc)
            SELECT NEWID(), i.ItemId, 'Deliver', NULL, @locationId, @operatorId, @stationId, @DeliveryId, @truckCode, i.Pieces, i.WeightLbs, @ArrivedAtUtc
            FROM logistics.DeliveryItems AS di
            JOIN dbo.Items AS i WITH (UPDLOCK) ON i.ItemId = di.ItemId
            WHERE di.DeliveryId = @DeliveryId
              AND i.Status = 'InTransit';

            UPDATE i
            SET Status = 'InYard',
                CurrentLocationId = @locationId,
                LastMovedAtUtc = CASE WHEN @ArrivedAtUtc > i.LastMovedAtUtc THEN @ArrivedAtUtc ELSE i.LastMovedAtUtc END
            FROM dbo.Items AS i
            JOIN logistics.DeliveryItems AS di ON di.ItemId = i.ItemId
            WHERE di.DeliveryId = @DeliveryId
              AND i.Status = 'InTransit';

            UPDATE logistics.Deliveries
            SET Status = 'Delivered', ArrivedAtUtc = @ArrivedAtUtc, ArrivalLocationId = @locationId
            WHERE DeliveryId = @DeliveryId;

            SELECT @outcome = 'Accepted', @message = CONCAT(N'Delivery #', @DeliveryId, N' unloaded at ', @ArrivalLocationCode, N'.');
        END

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH

    SELECT @outcome AS Outcome, @message AS Message, d.DeliveryId,
           (SELECT COUNT(*) FROM logistics.DeliveryItems AS di WHERE di.DeliveryId = d.DeliveryId) AS ItemCount,
           (SELECT ISNULL(SUM(i.WeightLbs), 0) FROM logistics.DeliveryItems AS di JOIN dbo.Items AS i ON i.ItemId = di.ItemId
             WHERE di.DeliveryId = d.DeliveryId) AS WeightLbs
    FROM (VALUES (1)) AS o (x)
    LEFT JOIN logistics.Deliveries AS d ON d.DeliveryId = @DeliveryId;
END
GO

CREATE OR ALTER PROCEDURE logistics.usp_RecordGpsPing
    @TruckCode      varchar(20),
    @Latitude       decimal(9,6),
    @Longitude      decimal(9,6),
    @SpeedMph       decimal(5,1)  = NULL,
    @HeadingDeg     smallint      = NULL,
    @RecordedAtUtc  datetime2(3)  = NULL
AS
BEGIN
    SET NOCOUNT ON;

    INSERT logistics.GpsPings (TruckId, DeliveryId, Latitude, Longitude, SpeedMph, HeadingDeg, RecordedAtUtc)
    SELECT t.TruckId, d.DeliveryId, @Latitude, @Longitude, @SpeedMph, @HeadingDeg, ISNULL(@RecordedAtUtc, SYSUTCDATETIME())
    FROM logistics.Trucks AS t
    LEFT JOIN logistics.Deliveries AS d ON d.TruckId = t.TruckId AND d.Status = 'InTransit'
    WHERE t.TruckCode = @TruckCode;

    IF @@ROWCOUNT = 0
        THROW 50020, 'Unknown truck code.', 1;
END
GO

CREATE OR ALTER PROCEDURE logistics.usp_GetFleetStatus
AS
BEGIN
    SET NOCOUNT ON;
    SELECT
        t.TruckCode,
        t.Description,
        d.DeliveryId,
        fs.SiteCode      AS FromSiteCode,
        ts.SiteCode      AS ToSiteCode,
        o.DisplayName    AS DriverName,
        d.DepartedAtUtc,
        (SELECT COUNT(*) FROM logistics.DeliveryItems AS di WHERE di.DeliveryId = d.DeliveryId) AS ItemCount,
        p.Latitude,
        p.Longitude,
        p.SpeedMph,
        p.HeadingDeg,
        p.RecordedAtUtc  AS LastPingAtUtc
    FROM logistics.Trucks AS t
    LEFT JOIN logistics.Deliveries AS d ON d.TruckId = t.TruckId AND d.Status = 'InTransit'
    LEFT JOIN dbo.Sites AS fs ON fs.SiteId = d.FromSiteId
    LEFT JOIN dbo.Sites AS ts ON ts.SiteId = d.ToSiteId
    LEFT JOIN dbo.Operators AS o ON o.OperatorId = d.DriverOperatorId
    OUTER APPLY (
        SELECT TOP (1) g.Latitude, g.Longitude, g.SpeedMph, g.HeadingDeg, g.RecordedAtUtc
        FROM logistics.GpsPings AS g
        WHERE g.TruckId = t.TruckId
        ORDER BY g.RecordedAtUtc DESC
    ) AS p
    WHERE t.IsActive = 1
    ORDER BY t.TruckCode;
END
GO

CREATE OR ALTER PROCEDURE logistics.usp_GetDeliveryRoute
    @DeliveryId int
AS
BEGIN
    SET NOCOUNT ON;
    SELECT t.TruckCode, g.Latitude, g.Longitude, g.SpeedMph, g.HeadingDeg, g.RecordedAtUtc
    FROM logistics.GpsPings AS g
    JOIN logistics.Trucks AS t ON t.TruckId = g.TruckId
    WHERE g.DeliveryId = @DeliveryId
    ORDER BY g.RecordedAtUtc;
END
GO

/* ----------------------------------------------------------------------------
   Telemetry
   ---------------------------------------------------------------------------- */
CREATE OR ALTER PROCEDURE telemetry.usp_GetMachines
AS
BEGIN
    SET NOCOUNT ON;
    SELECT m.MachineCode, m.Name, m.MachineType, l.LocationCode, m.ModbusUnitId, m.TempWarnC, m.TempAlarmC
    FROM telemetry.Machines AS m
    JOIN dbo.Locations AS l ON l.LocationId = m.LocationId
    WHERE m.IsActive = 1
    ORDER BY m.ModbusUnitId;
END
GO

CREATE OR ALTER PROCEDURE telemetry.usp_RecordMachineReadings
    @Readings telemetry.MachineReadingList READONLY
AS
BEGIN
    SET NOCOUNT ON;

    INSERT telemetry.MachineReadings (MachineId, RecordedAtUtc, TemperatureC, StatusCode, CycleCount, FaultCode)
    SELECT m.MachineId, r.RecordedAtUtc, r.TemperatureC, r.StatusCode, r.CycleCount, r.FaultCode
    FROM @Readings AS r
    JOIN telemetry.Machines AS m ON m.MachineCode = r.MachineCode
    JOIN telemetry.MachineStatuses AS s ON s.StatusCode = r.StatusCode;

    SELECT @@ROWCOUNT AS RowsInserted;
END
GO

CREATE OR ALTER PROCEDURE telemetry.usp_GetMachineStatus
AS
BEGIN
    SET NOCOUNT ON;
    SELECT
        m.MachineCode,
        m.Name,
        m.MachineType,
        l.LocationCode,
        r.StatusCode,
        ISNULL(s.StatusName, 'NoData') AS StatusName,
        r.TemperatureC,
        m.TempWarnC,
        m.TempAlarmC,
        r.CycleCount,
        r.FaultCode,
        r.RecordedAtUtc AS LastReadingAtUtc
    FROM telemetry.Machines AS m
    JOIN dbo.Locations AS l ON l.LocationId = m.LocationId
    OUTER APPLY (
        SELECT TOP (1) mr.StatusCode, mr.TemperatureC, mr.CycleCount, mr.FaultCode, mr.RecordedAtUtc
        FROM telemetry.MachineReadings AS mr
        WHERE mr.MachineId = m.MachineId
        ORDER BY mr.RecordedAtUtc DESC
    ) AS r
    LEFT JOIN telemetry.MachineStatuses AS s ON s.StatusCode = r.StatusCode
    WHERE m.IsActive = 1
    ORDER BY m.ModbusUnitId;
END
GO
