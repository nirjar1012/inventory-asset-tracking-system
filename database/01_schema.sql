/* =============================================================================
   YardTracker — 01_schema.sql
   Core relational schema for a pipe/steel yard inventory tracking system.

   Schemas
     dbo        Operational (OLTP) tables written by the WCF service
     logistics  Truck deliveries between sites + GPS pings
     telemetry  Machine readings collected over Modbus TCP
     etl        Watermarks, run log and staging used by the SSIS package
     rpt        Reporting tables/views consumed by SSRS and Power BI

   Conventions
     * All timestamps are UTC (suffix Utc). Reporting converts to the site's
       local "business day" using Sites.TimeZoneId.
     * ScanEvents is insert-only. Items holds current state.
     * ClientScanId is generated on the handheld so retries over a flaky
       wireless link are idempotent.
   ============================================================================= */
SET XACT_ABORT ON;
GO

CREATE SCHEMA logistics AUTHORIZATION dbo;
GO
CREATE SCHEMA telemetry AUTHORIZATION dbo;
GO
CREATE SCHEMA etl AUTHORIZATION dbo;
GO
CREATE SCHEMA rpt AUTHORIZATION dbo;
GO

/* ----------------------------------------------------------------------------
   Reference data
   ---------------------------------------------------------------------------- */
CREATE TABLE dbo.Sites
(
    SiteId      int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Sites PRIMARY KEY,
    SiteCode    varchar(10)       NOT NULL CONSTRAINT UQ_Sites_SiteCode UNIQUE,
    Name        nvarchar(100)     NOT NULL,
    Latitude    decimal(9,6)      NOT NULL,
    Longitude   decimal(9,6)      NOT NULL,
    TimeZoneId  varchar(50)       NOT NULL CONSTRAINT DF_Sites_TimeZoneId DEFAULT ('Central Standard Time'),
    IsActive    bit               NOT NULL CONSTRAINT DF_Sites_IsActive DEFAULT (1)
);

CREATE TABLE dbo.Locations
(
    LocationId    int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Locations PRIMARY KEY,
    SiteId        int               NOT NULL CONSTRAINT FK_Locations_Sites REFERENCES dbo.Sites (SiteId),
    LocationCode  varchar(20)       NOT NULL CONSTRAINT UQ_Locations_LocationCode UNIQUE,
    Name          nvarchar(100)     NOT NULL,
    LocationType  varchar(20)       NOT NULL
        CONSTRAINT CK_Locations_LocationType CHECK (LocationType IN ('Rack', 'Laydown', 'Bay', 'Dock', 'Staging', 'Quarantine')),
    CapacityTons  decimal(9,2)      NULL,
    IsActive      bit               NOT NULL CONSTRAINT DF_Locations_IsActive DEFAULT (1)
);
CREATE INDEX IX_Locations_SiteId ON dbo.Locations (SiteId);

CREATE TABLE dbo.Operators
(
    OperatorId    int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Operators PRIMARY KEY,
    BadgeNumber   varchar(20)       NOT NULL CONSTRAINT UQ_Operators_BadgeNumber UNIQUE,
    DisplayName   nvarchar(100)     NOT NULL,
    Role          varchar(20)       NOT NULL
        CONSTRAINT CK_Operators_Role CHECK (Role IN ('Operator', 'Supervisor', 'Driver')),
    IsActive      bit               NOT NULL CONSTRAINT DF_Operators_IsActive DEFAULT (1),
    CreatedAtUtc  datetime2(3)      NOT NULL CONSTRAINT DF_Operators_CreatedAtUtc DEFAULT (SYSUTCDATETIME())
);

/* A station is a scanning device: a wireless handheld, a fixed RFID portal, or a kiosk PC. */
CREATE TABLE dbo.Stations
(
    StationId          int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Stations PRIMARY KEY,
    StationCode        varchar(20)       NOT NULL CONSTRAINT UQ_Stations_StationCode UNIQUE,
    Name               nvarchar(100)     NOT NULL,
    DeviceType         varchar(20)       NOT NULL
        CONSTRAINT CK_Stations_DeviceType CHECK (DeviceType IN ('Handheld', 'FixedReader', 'Kiosk')),
    DefaultLocationId  int               NULL CONSTRAINT FK_Stations_Locations REFERENCES dbo.Locations (LocationId),
    LastSeenAtUtc      datetime2(3)      NULL,
    IsActive           bit               NOT NULL CONSTRAINT DF_Stations_IsActive DEFAULT (1)
);

CREATE TABLE dbo.Products
(
    ProductId        int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Products PRIMARY KEY,
    ProductCode      varchar(30)       NOT NULL CONSTRAINT UQ_Products_ProductCode UNIQUE,
    Category         varchar(20)       NOT NULL
        CONSTRAINT CK_Products_Category CHECK (Category IN ('Pipe', 'Casing', 'Tubing', 'Plate', 'Beam', 'Bar')),
    Description      nvarchar(200)     NOT NULL,
    Grade            varchar(20)       NOT NULL,
    OuterDiameterIn  decimal(7,3)      NULL,
    WallThicknessIn  decimal(6,3)      NULL,
    NominalLengthFt  decimal(7,2)      NOT NULL CONSTRAINT CK_Products_NominalLengthFt CHECK (NominalLengthFt > 0),
    WeightLbsPerFt   decimal(9,3)      NOT NULL CONSTRAINT CK_Products_WeightLbsPerFt CHECK (WeightLbsPerFt > 0)
);

/* ----------------------------------------------------------------------------
   Inventory
   ---------------------------------------------------------------------------- */

/* One tagged unit: a bundle of pipe joints, a plate, a beam. TagId is the
   barcode value or RFID EPC printed/encoded on the tag. HeatNumber ties the
   steel back to the mill test report for traceability. */
CREATE TABLE dbo.Items
(
    ItemId             int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Items PRIMARY KEY,
    TagId              varchar(32)       NOT NULL CONSTRAINT UQ_Items_TagId UNIQUE,
    ProductId          int               NOT NULL CONSTRAINT FK_Items_Products REFERENCES dbo.Products (ProductId),
    HeatNumber         varchar(20)       NOT NULL,
    Pieces             int               NOT NULL CONSTRAINT CK_Items_Pieces CHECK (Pieces > 0),
    TotalLengthFt      decimal(9,2)      NOT NULL CONSTRAINT CK_Items_TotalLengthFt CHECK (TotalLengthFt > 0),
    WeightLbs          decimal(12,2)     NOT NULL CONSTRAINT CK_Items_WeightLbs CHECK (WeightLbs > 0),
    Status             varchar(12)       NOT NULL
        CONSTRAINT CK_Items_Status CHECK (Status IN ('InYard', 'CheckedOut', 'InTransit')),
    CurrentLocationId  int               NULL CONSTRAINT FK_Items_Locations REFERENCES dbo.Locations (LocationId),
    LastMovedAtUtc     datetime2(3)      NOT NULL,
    CreatedAtUtc       datetime2(3)      NOT NULL CONSTRAINT DF_Items_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
    RowVer             rowversion        NOT NULL,
    -- An item has a physical yard location only while it is in the yard.
    CONSTRAINT CK_Items_LocationMatchesStatus CHECK (
        (Status = 'InYard' AND CurrentLocationId IS NOT NULL) OR
        (Status <> 'InYard' AND CurrentLocationId IS NULL))
);
CREATE INDEX IX_Items_CurrentLocationId ON dbo.Items (CurrentLocationId) INCLUDE (Status, Pieces, WeightLbs, LastMovedAtUtc);
CREATE INDEX IX_Items_Status_LastMoved ON dbo.Items (Status, LastMovedAtUtc) INCLUDE (CurrentLocationId);

CREATE TABLE logistics.Trucks
(
    TruckId        int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Trucks PRIMARY KEY,
    TruckCode      varchar(20)       NOT NULL CONSTRAINT UQ_Trucks_TruckCode UNIQUE,
    Description    nvarchar(100)     NOT NULL,
    MaxPayloadLbs  decimal(10,0)     NOT NULL,
    IsActive       bit               NOT NULL CONSTRAINT DF_Trucks_IsActive DEFAULT (1)
);

CREATE TABLE logistics.Deliveries
(
    DeliveryId         int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Deliveries PRIMARY KEY,
    ClientRequestId    uniqueidentifier  NOT NULL CONSTRAINT UQ_Deliveries_ClientRequestId UNIQUE,
    TruckId            int               NOT NULL CONSTRAINT FK_Deliveries_Trucks REFERENCES logistics.Trucks (TruckId),
    DriverOperatorId   int               NOT NULL CONSTRAINT FK_Deliveries_Operators REFERENCES dbo.Operators (OperatorId),
    FromSiteId         int               NOT NULL CONSTRAINT FK_Deliveries_FromSite REFERENCES dbo.Sites (SiteId),
    ToSiteId           int               NOT NULL CONSTRAINT FK_Deliveries_ToSite REFERENCES dbo.Sites (SiteId),
    Status             varchar(12)       NOT NULL
        CONSTRAINT CK_Deliveries_Status CHECK (Status IN ('InTransit', 'Delivered')),
    DepartedAtUtc      datetime2(3)      NOT NULL,
    ArrivedAtUtc       datetime2(3)      NULL,
    ArrivalLocationId  int               NULL CONSTRAINT FK_Deliveries_ArrivalLocation REFERENCES dbo.Locations (LocationId),
    CONSTRAINT CK_Deliveries_DifferentSites CHECK (FromSiteId <> ToSiteId)
);
-- A truck can only be on one active delivery at a time.
CREATE UNIQUE INDEX UX_Deliveries_ActivePerTruck ON logistics.Deliveries (TruckId) WHERE Status = 'InTransit';

CREATE TABLE logistics.DeliveryItems
(
    DeliveryId  int NOT NULL CONSTRAINT FK_DeliveryItems_Deliveries REFERENCES logistics.Deliveries (DeliveryId),
    ItemId      int NOT NULL CONSTRAINT FK_DeliveryItems_Items REFERENCES dbo.Items (ItemId),
    CONSTRAINT PK_DeliveryItems PRIMARY KEY (DeliveryId, ItemId)
);
CREATE INDEX IX_DeliveryItems_ItemId ON logistics.DeliveryItems (ItemId);

/* Insert-only movement log. Every accepted scan is one row. */
CREATE TABLE dbo.ScanEvents
(
    ScanEventId     bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_ScanEvents PRIMARY KEY,
    ClientScanId    uniqueidentifier     NOT NULL CONSTRAINT UQ_ScanEvents_ClientScanId UNIQUE,
    ItemId          int                  NOT NULL CONSTRAINT FK_ScanEvents_Items REFERENCES dbo.Items (ItemId),
    EventType       varchar(12)          NOT NULL
        CONSTRAINT CK_ScanEvents_EventType CHECK (EventType IN ('Receive', 'CheckIn', 'CheckOut', 'Move', 'LoadTruck', 'Deliver')),
    FromLocationId  int                  NULL CONSTRAINT FK_ScanEvents_FromLocation REFERENCES dbo.Locations (LocationId),
    ToLocationId    int                  NULL CONSTRAINT FK_ScanEvents_ToLocation REFERENCES dbo.Locations (LocationId),
    OperatorId      int                  NOT NULL CONSTRAINT FK_ScanEvents_Operators REFERENCES dbo.Operators (OperatorId),
    StationId       int                  NULL CONSTRAINT FK_ScanEvents_Stations REFERENCES dbo.Stations (StationId),
    DeliveryId      int                  NULL CONSTRAINT FK_ScanEvents_Deliveries REFERENCES logistics.Deliveries (DeliveryId),
    Reference       nvarchar(50)         NULL,       -- work order, BOL, PO
    Pieces          int                  NOT NULL,   -- snapshot at scan time
    WeightLbs       decimal(12,2)        NOT NULL,   -- snapshot at scan time
    ScannedAtUtc    datetime2(3)         NOT NULL,   -- device clock (may be hours old if the handheld was offline)
    ReceivedAtUtc   datetime2(3)         NOT NULL CONSTRAINT DF_ScanEvents_ReceivedAtUtc DEFAULT (SYSUTCDATETIME()),
    RowVer          rowversion           NOT NULL    -- ETL watermark (see etl.usp_BeginRun)
);
CREATE INDEX IX_ScanEvents_ItemId_ScannedAt ON dbo.ScanEvents (ItemId, ScannedAtUtc);
CREATE INDEX IX_ScanEvents_ScannedAt ON dbo.ScanEvents (ScannedAtUtc) INCLUDE (EventType, FromLocationId, ToLocationId, ItemId, OperatorId, WeightLbs);
CREATE UNIQUE INDEX UX_ScanEvents_RowVer ON dbo.ScanEvents (RowVer);
CREATE INDEX IX_ScanEvents_StationId ON dbo.ScanEvents (StationId, ScanEventId DESC);

/* Rejected scans. Supervisors review these; a daily report is published to SharePoint. */
CREATE TABLE dbo.ScanExceptions
(
    ScanExceptionId  bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_ScanExceptions PRIMARY KEY,
    ClientScanId     uniqueidentifier     NULL,
    RawTag           varchar(64)          NOT NULL,
    AttemptedAction  varchar(12)          NOT NULL,
    ExceptionType    varchar(30)          NOT NULL
        CONSTRAINT CK_ScanExceptions_ExceptionType CHECK (ExceptionType IN (
            'UnknownTag', 'UnknownLocation', 'UnknownOperator', 'UnknownStation', 'UnknownProduct',
            'InvalidState', 'DuplicateTag', 'ClockSkew', 'WrongSite')),
    Message          nvarchar(400)        NOT NULL,
    ItemId           int                  NULL CONSTRAINT FK_ScanExceptions_Items REFERENCES dbo.Items (ItemId),
    OperatorId       int                  NULL CONSTRAINT FK_ScanExceptions_Operators REFERENCES dbo.Operators (OperatorId),
    StationId        int                  NULL CONSTRAINT FK_ScanExceptions_Stations REFERENCES dbo.Stations (StationId),
    LocationId       int                  NULL CONSTRAINT FK_ScanExceptions_Locations REFERENCES dbo.Locations (LocationId),
    OccurredAtUtc    datetime2(3)         NOT NULL CONSTRAINT DF_ScanExceptions_OccurredAtUtc DEFAULT (SYSUTCDATETIME()),
    ResolvedAtUtc    datetime2(3)         NULL,
    ResolvedBy       nvarchar(100)        NULL,
    PublishedAtUtc   datetime2(3)         NULL   -- set when included in a SharePoint exception report
);
CREATE UNIQUE INDEX UX_ScanExceptions_ClientScanId ON dbo.ScanExceptions (ClientScanId) WHERE ClientScanId IS NOT NULL;
CREATE INDEX IX_ScanExceptions_OccurredAt ON dbo.ScanExceptions (OccurredAtUtc) INCLUDE (ExceptionType, ResolvedAtUtc);

CREATE TABLE logistics.GpsPings
(
    GpsPingId      bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_GpsPings PRIMARY KEY,
    TruckId        int                  NOT NULL CONSTRAINT FK_GpsPings_Trucks REFERENCES logistics.Trucks (TruckId),
    DeliveryId     int                  NULL CONSTRAINT FK_GpsPings_Deliveries REFERENCES logistics.Deliveries (DeliveryId),
    Latitude       decimal(9,6)         NOT NULL CONSTRAINT CK_GpsPings_Latitude CHECK (Latitude BETWEEN -90 AND 90),
    Longitude      decimal(9,6)         NOT NULL CONSTRAINT CK_GpsPings_Longitude CHECK (Longitude BETWEEN -180 AND 180),
    SpeedMph       decimal(5,1)         NULL,
    HeadingDeg     smallint             NULL CONSTRAINT CK_GpsPings_Heading CHECK (HeadingDeg BETWEEN 0 AND 359),
    RecordedAtUtc  datetime2(3)         NOT NULL,
    ReceivedAtUtc  datetime2(3)         NOT NULL CONSTRAINT DF_GpsPings_ReceivedAtUtc DEFAULT (SYSUTCDATETIME())
);
CREATE INDEX IX_GpsPings_Truck_RecordedAt ON logistics.GpsPings (TruckId, RecordedAtUtc DESC) INCLUDE (Latitude, Longitude, SpeedMph, HeadingDeg, DeliveryId);
CREATE INDEX IX_GpsPings_DeliveryId ON logistics.GpsPings (DeliveryId, RecordedAtUtc) WHERE DeliveryId IS NOT NULL;

/* ----------------------------------------------------------------------------
   Telemetry (Modbus TCP → edge gateway → WCF → SQL)
   ---------------------------------------------------------------------------- */
CREATE TABLE telemetry.MachineStatuses
(
    StatusCode  smallint     NOT NULL CONSTRAINT PK_MachineStatuses PRIMARY KEY,
    StatusName  varchar(20)  NOT NULL
);

CREATE TABLE telemetry.Machines
(
    MachineId     int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Machines PRIMARY KEY,
    MachineCode   varchar(20)       NOT NULL CONSTRAINT UQ_Machines_MachineCode UNIQUE,
    Name          nvarchar(100)     NOT NULL,
    MachineType   varchar(20)       NOT NULL
        CONSTRAINT CK_Machines_MachineType CHECK (MachineType IN ('Crane', 'Saw', 'CoatingOven', 'Threader')),
    LocationId    int               NOT NULL CONSTRAINT FK_Machines_Locations REFERENCES dbo.Locations (LocationId),
    ModbusUnitId  tinyint           NOT NULL CONSTRAINT UQ_Machines_ModbusUnitId UNIQUE,
    TempWarnC     decimal(6,1)      NULL,
    TempAlarmC    decimal(6,1)      NULL,
    IsActive      bit               NOT NULL CONSTRAINT DF_Machines_IsActive DEFAULT (1)
);

CREATE TABLE telemetry.MachineReadings
(
    MachineReadingId  bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_MachineReadings PRIMARY KEY,
    MachineId         int                  NOT NULL CONSTRAINT FK_MachineReadings_Machines REFERENCES telemetry.Machines (MachineId),
    RecordedAtUtc     datetime2(3)         NOT NULL,
    TemperatureC      decimal(6,1)         NULL,
    StatusCode        smallint             NOT NULL CONSTRAINT FK_MachineReadings_Statuses REFERENCES telemetry.MachineStatuses (StatusCode),
    CycleCount        bigint               NULL,
    FaultCode         smallint             NOT NULL CONSTRAINT DF_MachineReadings_FaultCode DEFAULT (0),
    ReceivedAtUtc     datetime2(3)         NOT NULL CONSTRAINT DF_MachineReadings_ReceivedAtUtc DEFAULT (SYSUTCDATETIME())
);
CREATE INDEX IX_MachineReadings_Machine_RecordedAt ON telemetry.MachineReadings (MachineId, RecordedAtUtc DESC)
    INCLUDE (TemperatureC, StatusCode, CycleCount, FaultCode);

/* ----------------------------------------------------------------------------
   Table-valued parameter types (used by the WCF service via ADO.NET)
   ---------------------------------------------------------------------------- */
CREATE TYPE dbo.TagList AS TABLE
(
    TagId varchar(64) NOT NULL PRIMARY KEY
);
GO

CREATE TYPE dbo.IdList AS TABLE
(
    Id bigint NOT NULL PRIMARY KEY
);
GO

CREATE TYPE telemetry.MachineReadingList AS TABLE
(
    MachineCode    varchar(20)   NOT NULL,
    RecordedAtUtc  datetime2(3)  NOT NULL,
    TemperatureC   decimal(6,1)  NULL,
    StatusCode     smallint      NOT NULL,
    CycleCount     bigint        NULL,
    FaultCode      smallint      NOT NULL
);
GO

/* ----------------------------------------------------------------------------
   ETL + reporting tables
   ---------------------------------------------------------------------------- */
CREATE TABLE etl.Watermarks
(
    ProcessName     varchar(50)   NOT NULL CONSTRAINT PK_Watermarks PRIMARY KEY,
    LastRowVersion  bigint        NOT NULL,
    UpdatedAtUtc    datetime2(3)  NOT NULL
);

CREATE TABLE etl.RunLog
(
    RunId           int IDENTITY(1,1) NOT NULL CONSTRAINT PK_RunLog PRIMARY KEY,
    ProcessName     varchar(50)       NOT NULL,
    RunSource       varchar(20)       NOT NULL,   -- 'SSIS' or 'StoredProc'
    Status          varchar(10)       NOT NULL CONSTRAINT CK_RunLog_Status CHECK (Status IN ('Running', 'Succeeded', 'Failed')),
    StartedAtUtc    datetime2(3)      NOT NULL,
    EndedAtUtc      datetime2(3)      NULL,
    FromRowVersion  bigint            NOT NULL,   -- exclusive
    ToRowVersion    bigint            NOT NULL,   -- inclusive
    RowsExtracted   int               NULL,
    RowsWritten     int               NULL,
    AffectedDays    int               NULL,
    ErrorMessage    nvarchar(2000)    NULL
);
CREATE INDEX IX_RunLog_Process_Started ON etl.RunLog (ProcessName, StartedAtUtc DESC);

/* Landing table for the SSIS data flow (truncated at the start of each run). */
CREATE TABLE etl.stg_ScanEvents
(
    ScanEventId      bigint         NOT NULL CONSTRAINT PK_stg_ScanEvents PRIMARY KEY,
    RowVersion       bigint         NOT NULL,
    ItemId           int            NOT NULL,
    EventType        varchar(12)    NOT NULL,
    FromLocationId   int            NULL,
    ToLocationId     int            NULL,
    OperatorId       int            NOT NULL,
    ScannedAtUtc     datetime2(3)   NOT NULL,
    Pieces           int            NOT NULL,
    WeightLbs        decimal(12,2)  NOT NULL,
    ProductCategory  varchar(20)    NOT NULL
);

CREATE TABLE rpt.DimDate
(
    DateKey          int          NOT NULL CONSTRAINT PK_DimDate PRIMARY KEY,   -- yyyymmdd
    [Date]           date         NOT NULL CONSTRAINT UQ_DimDate_Date UNIQUE,
    [Year]           smallint     NOT NULL,
    [Quarter]        tinyint      NOT NULL,
    MonthNumber      tinyint      NOT NULL,
    MonthName        varchar(10)  NOT NULL,
    YearMonth        char(7)      NOT NULL,   -- 2026-09
    IsoWeek          tinyint      NOT NULL,
    DayOfWeekNumber  tinyint      NOT NULL,   -- 1 = Monday
    DayName          varchar(10)  NOT NULL,
    IsWeekend        bit          NOT NULL
);

/* Daily inventory movement roll-up. Grain: business day (site local) × location × product category. */
CREATE TABLE rpt.DailyInventoryMovement
(
    BusinessDate       date           NOT NULL,
    LocationId         int            NOT NULL CONSTRAINT FK_DailyInventoryMovement_Locations REFERENCES dbo.Locations (LocationId),
    ProductCategory    varchar(20)    NOT NULL,
    Receipts           int            NOT NULL,
    CheckIns           int            NOT NULL,
    CheckOuts          int            NOT NULL,
    MovesIn            int            NOT NULL,
    MovesOut           int            NOT NULL,
    TruckLoads         int            NOT NULL,
    TruckDeliveries    int            NOT NULL,
    InboundEvents      int            NOT NULL,
    OutboundEvents     int            NOT NULL,
    InboundTons        decimal(12,3)  NOT NULL,
    OutboundTons       decimal(12,3)  NOT NULL,
    DistinctItems      int            NOT NULL,
    DistinctOperators  int            NOT NULL,
    FirstScanAtUtc     datetime2(3)   NOT NULL,
    LastScanAtUtc      datetime2(3)   NOT NULL,
    LoadedByRunId      int            NOT NULL,
    LoadedAtUtc        datetime2(3)   NOT NULL,
    CONSTRAINT PK_DailyInventoryMovement PRIMARY KEY (BusinessDate, LocationId, ProductCategory)
);
GO
