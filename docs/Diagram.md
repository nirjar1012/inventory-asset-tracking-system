YardTracker · inventory-asset-tracking-system

# Flow Atlas

Every workflow in the YardTracker system as a diagram: how pipe and steel bundles are scanned, trucked between sites, and watched by plant machines, and how that data reaches SSRS, Power BI and SharePoint. Each figure names the doc or source file it was drawn from.

## System

- [System overview](#overview)
- [Service contracts](#contracts)
- [Data model](#data-model)
- [Item state machine](#states)

## Floor workflows

- [Station session](#station)
- [One scan, end to end](#scan)
- [Offline scan replay](#offline)
- [Truck delivery](#delivery)
- [Machine fault](#telemetry)

## Back office

- [ETL run](#etl)
- [Reporting](#reporting)
- [Exception publishing](#sharepoint)
- [Exception feedback loop](#loop)

## Dev and ops

- [Simulator modes](#simulator)
- [Build and run order](#runbook)

## System what exists and how it connects

### System overview

Flowchart

Three kinds of floor device feed one WCF service. SQL Server enforces the rules, and everything downstream reads from it.

README.md · docs/architecture.md

```mermaid
%%{init: {'theme':'neutral'}}%%
flowchart LR
  subgraph floor["Yard floor"]
    station["WPF scanning station"]
    driver["Driver handheld"]
    plc["Machine PLCs"]
  end
  gw["Edge gateway, .NET 10 worker"]
  svc["WCF service, net.tcp 8523"]
  db[("SQL Server: YardTracker")]
  ssis["SSIS: DailyInventoryMovement"]
  ssrs["SSRS reports"]
  pbi["Power BI dashboard"]
  sp["SharePoint: reports and SOPs"]
  station -- "check in, out, move, receive" --> svc
  driver -- "deliveries and GPS pings" --> svc
  plc -- "Modbus FC03" --> gw
  gw -- "RecordReadings" --> svc
  svc -- "stored procedures" --> db
  db -- "rowversion extract" --> ssis
  ssis -- "rpt tables" --> db
  db --> ssrs
  db --> pbi
  db -- "daily exception job" --> sp
```

**Service ports**8523 for net.tcp, 8524 for metadata (MEX)

**PLC port**502 on real PLCs, 5020 for the simulator

**Security**Windows transport security: signed and encrypted

### Service contracts by consumer

Flowchart

Contracts are split by who calls them, not by table. A gateway has no channel that can move inventory.

docs/04-service-and-contracts.md · src/YardTracker.Contracts/ServiceContracts.cs

```mermaid
%%{init: {'theme':'neutral'}}%%
flowchart LR
  st["Scanning stations: handhelds, RFID portals, kiosks"] --> inv["IInventoryService"]
  dr["Driver handhelds and fleet map"] --> lgs["ILogisticsService"]
  gw["Modbus edge gateway"] --> tel["ITelemetryService"]
  inv --> dbo[("dbo schema")]
  lgs --> lg[("logistics schema")]
  tel --> tm[("telemetry schema")]
  inv -.- invops["SignIn, LookupItem, ReceiveItem, CheckInItem, CheckOutItem, MoveItem, GetItemHistory, GetOpenExceptions"]
  lgs -.- lgsops["StartDelivery, CompleteDelivery, ReportPosition, GetFleetStatus, GetDeliveryRoute"]
  tel -.- telops["GetMachines, RecordReadings, GetMachineStatus"]
```

### Data model

ER diagram

`Items` holds current state. `ScanEvents` is the insert-only history of how each item got there.

docs/architecture.md · database/01_schema.sql

```mermaid
%%{init: {'theme':'neutral'}}%%
erDiagram
  Sites ||--o{ Locations : contains
  Locations ||--o{ Items : "currently holds"
  Products ||--o{ Items : "is a"
  Items ||--o{ ScanEvents : "movement log"
  Operators ||--o{ ScanEvents : scans
  Stations ||--o{ ScanEvents : "scanned on"
  Items ||--o{ ScanExceptions : "rejected scans"
  Trucks ||--o{ Deliveries : runs
  Deliveries ||--o{ DeliveryItems : carries
  Deliveries ||--o{ GpsPings : breadcrumbs
  Machines ||--o{ MachineReadings : reports
  Locations ||--o{ DailyInventoryMovement : "rolled up by ETL"
```

**dbo**Operational scans and items

**logistics**Trucks, deliveries, GPS

**telemetry**Machine readings from Modbus

**etl · rpt**Staging and control, then the reporting contract

### Item state machine

State diagram

Every scan is checked against these transitions. Anything else is rejected with a reason and written to `dbo.ScanExceptions`.

docs/architecture.md · database/02_operations.sql

```mermaid
%%{init: {'theme':'neutral'}}%%
stateDiagram-v2
  [*] --> InYard : Receive, new tag
  InYard --> InYard : Move, same site
  InYard --> CheckedOut : CheckOut, work order or BOL
  CheckedOut --> InYard : CheckIn, return
  InYard --> InTransit : StartDelivery, truck load
  InTransit --> InYard : CompleteDelivery, destination site only
```

**Rejection reasons**UnknownTag, InvalidState, WrongSite, UnknownLocation, UnknownOperator, ClockSkew, DuplicateTag

## Floor workflows what happens on a device

### Station session

Flowchart

An operator signs in with a badge, then works from four tabs. The Scan tab has five modes.

docs/05-station-app.md · src/YardTracker.Station

```mermaid
%%{init: {'theme':'neutral'}}%%
flowchart TD
  launch["Launch station with a station code, e.g. HH-01"] --> cfg["Load station.json settings"]
  cfg --> signin["Sign-in screen: scan a BADGE label or type 1001"]
  signin -- "SignInAsync" --> session["StationSession: name, role, default location, server time"]
  session --> tabs{"Main tabs"}
  tabs --> scan["Scan"]
  tabs --> inv["Inventory"]
  tabs --> fleet["Fleet map"]
  tabs --> mach["Machines"]
  scan --> m1["Receive"]
  scan --> m2["Check in"]
  scan --> m3["Check out"]
  scan --> m4["Move"]
  scan --> m5["Lookup"]
  fleet -. "polls GetFleetStatus" .-> svc["WCF service"]
  mach -. "polls GetMachineStatus" .-> svc
```

**Demo badges**1001 operator · 2001 supervisor · 3001 driver

**Scanner input**Keyboard-wedge reads are rerouted to the scan box if focus is elsewhere

### One scan, from tag to database

Sequence

An operator moves a bundle from the receiving dock to rack 3. The idempotency key is created on the device, and the database decides the outcome.

docs/12-end-to-end-walkthroughs.md, walkthrough 1

```mermaid
%%{init: {'theme':'neutral'}}%%
sequenceDiagram
  autonumber
  actor Op as Operator
  participant St as Station
  participant Cl as YardTrackerClient
  participant Svc as WCF service
  participant DB as usp_RecordScan
  Op->>St: Scan location label LOC:NYD-R03
  St->>St: ScanCodes.Parse switches target location, no server call
  Op->>St: Scan bundle tag YT-004211
  St->>St: Build ScanRequest with new ClientScanId and device time
  St->>Cl: ScanSubmitter.SubmitAsync
  Cl->>Svc: MoveItem over net.tcp
  Svc->>Svc: Operation.Run and Guard validate input
  Svc->>DB: usp_MoveItem calls usp_RecordScan
  alt ClientScanId already processed
    DB-->>Svc: Duplicate, original answer returned
  else operator, station, location or clock invalid
    DB->>DB: Log to ScanExceptions
    DB-->>Svc: Rejected with reason
  else valid transition
    DB->>DB: UPDLOCK item row, check state machine
    DB->>DB: Update Items, insert ScanEvents, commit
    DB-->>Svc: Accepted plus item card
  end
  Svc-->>Cl: ScanResult
  Cl-->>St: Result
  St-->>Op: Banner, item card, refocus scan box
```

**Tag to database**A few milliseconds

**Tag to dashboard**One ETL cycle, about 15 minutes

### Offline scan replay

Sequence

With no Wi-Fi, scans queue on the device in order and replay later with their original IDs and timestamps. A lost reply cannot move an item twice.

docs/12-end-to-end-walkthroughs.md, walkthrough 2 · docs/05-station-app.md

```mermaid
%%{init: {'theme':'neutral'}}%%
sequenceDiagram
  participant St as Station
  participant Q as OfflineScanQueue
  participant Svc as WCF service
  participant DB as SQL Server
  St->>Svc: MoveItem
  Svc --x St: SocketException or timeout after 5 s
  St->>Q: Enqueue, write tmp file then File.Move
  Note over St: Header shows Offline, 1 pending
  St->>Q: Later scans queue behind it, order kept
  loop every OfflineRetrySeconds
    St->>Q: FlushAsync takes oldest scan
    St->>Svc: Send with original ClientScanId and ScannedAtUtc
  end
  Svc->>DB: usp_RecordScan
  DB-->>Svc: Accepted
  Svc --x St: Reply lost on the way back
  St->>Svc: Send the same scan again
  Svc->>DB: usp_RecordScan
  DB-->>Svc: Duplicate, nothing moves twice
  Svc-->>St: Amber banner, Already recorded
  St->>Q: Remove from queue
```

**Queue file**%LOCALAPPDATA%\\YardTracker\\offline-queue-\<station>.json

**Late data in ETL**The rowversion watermark still picks it up and rebuilds the day it happened

### Truck delivery between sites

Sequence

Three bundles go from North Yard to the Coating and Threading Plant. The whole load is validated at once, so a half-loaded truck cannot exist.

docs/12-end-to-end-walkthroughs.md, walkthrough 3

```mermaid
%%{init: {'theme':'neutral'}}%%
sequenceDiagram
  actor Dr as Driver on HH-DRV1
  participant Svc as ILogisticsService
  participant DB as SQL Server
  participant Map as Station fleet map
  Dr->>Svc: StartDelivery, TRK-101 with 3 tags
  Svc->>DB: logistics.usp_StartDelivery
  DB->>DB: Lock all items, build one problem list
  alt any problem
    DB-->>Svc: Rejected with full list, nothing changes
  else load is valid
    DB->>DB: Delivery InTransit, items InTransit, LoadTruck events
    DB-->>Svc: Accepted
  end
  loop every few seconds on the road
    Dr->>Svc: ReportPosition
    Svc->>DB: usp_RecordGpsPing on the active delivery
    Map->>Svc: GetFleetStatus
    Svc-->>Map: Truck positions and trail
  end
  Dr->>Svc: CompleteDelivery at arrival location
  Svc->>DB: Check location belongs to destination site
  DB->>DB: Items InYard, delivery Delivered, Deliver events
  DB-->>Svc: Accepted
```

**Load checks**Unknown tag, not InYard, wrong origin site, truck busy, over MaxPayloadLbs

**One active load per truck**Enforced by the unique filtered index UX_Deliveries_ActivePerTruck

### Machine fault, PLC to dashboard

Sequence

The CNC threader trips at 02:14. The gateway polls it over Modbus TCP, logs the state change once, and forwards readings in batches.

docs/12-end-to-end-walkthroughs.md, walkthrough 4 · docs/06-modbus-and-gateway.md

```mermaid
%%{init: {'theme':'neutral'}}%%
sequenceDiagram
  participant PLC as PLC unit 4, THR-C1
  participant GW as Edge gateway
  participant Svc as ITelemetryService
  participant DB as SQL Server
  participant St as Station machines tab
  PLC->>PLC: MachineModel.Step sets Fault, code 402
  loop every 5 s
    GW->>PLC: FC03 read registers 40001 to 40005
    PLC-->>GW: Temperature, state, cycle count, fault code
    GW->>GW: Decode, log a warning only when state changes
    GW->>GW: Buffer reading with its own timestamp
  end
  GW->>Svc: RecordReadings batch
  alt service down
    GW->>GW: Keep buffer, send when service returns
  else service up
    Svc->>DB: telemetry.usp_RecordMachineReadings via TVP
  end
  St->>Svc: GetMachineStatus
  Svc-->>St: THR-C1 Fault, tile turns red
  opt AutoResetFaultsAfterPolls above 0
    GW->>PLC: FC06 write 1 to register 40011
    PLC-->>GW: Fault cleared
  end
```

**Register 40001**Temperature in tenths of °C, 0x0226 = 55.0 °C

**Units**1 crane · 2 band saw · 3 cure oven · 4 threader

**Threader limits**Warn 65 °C, alarm 80 °C

## Back office what happens to the data

### ETL run: DailyInventoryMovement

Flowchart

Each run reads scan events committed since the last watermark and rebuilds only the (day, location) cells they touch. Reruns give the same result.

docs/08-etl-pipeline.md · etl/ssis · database/03_etl_reporting.sql

```mermaid
%%{init: {'theme':'neutral'}}%%
flowchart LR
  trig["SQL Agent job or Run-Etl.ps1"] --> begin["etl.usp_BeginRun: applock, RunLog row, from = watermark, to = MIN_ACTIVE_ROWVERSION - 1"]
  begin --> extract["Data flow: etl.fn_ExtractScanEvents into etl.stg_ScanEvents"]
  extract --> merge["etl.usp_MergeDailyInventoryMovement: delete and rebuild touched cells"]
  merge --> rpt[("rpt.DailyInventoryMovement")]
  merge --> wm["Advance watermark, RunLog Succeeded"]
  extract -. "error" .-> fail["OnError: etl.usp_FailRun"]
  merge -. "error" .-> fail
```

**Why rowversion**Device time is not arrival order, so a time watermark would skip late offline scans

**Grain**Business date × location × product category

**Moves**Counted as MovesOut at origin and MovesIn at destination

### Reporting

Flowchart

SSRS and Power BI read only the `rpt` schema, so the operational tables can change without breaking a report.

docs/09-reporting.md · reports/ssrs · powerbi

```mermaid
%%{init: {'theme':'neutral'}}%%
flowchart LR
  subgraph rptschema["rpt schema"]
    dim["DimDate"]
    dim2["DailyInventoryMovement"]
    views["Views: current inventory, location stays, deliveries, machine hourly"]
  end
  rptschema --> ssrs["SSRS: operational"]
  ssrs --> r1["Current Inventory by Location"]
  ssrs --> r2["Items Overdue for Movement"]
  ssrs --> r3["Daily Yard Throughput"]
  rptschema --> pbi["Power BI model: 33 measures"]
  pbi --> p1["Yard Overview"]
  pbi --> p2["Flow and Bottlenecks"]
  pbi --> p3["Machines and Trucks"]
```

### Daily exception publishing

Flowchart

Each morning after the ETL, yesterday's rejected scans are published to SharePoint as a report and a worklist. Running it twice is safe.

docs/10-sharepoint-and-sops.md · sharepoint/Publish-ScanExceptionReport.ps1

```mermaid
%%{init: {'theme':'neutral'}}%%
flowchart LR
  extract["rpt.usp_ScanExceptionReport for the business date"] --> render["Render HTML and CSV to out/sharepoint"]
  render --> local{"LocalOnly?"}
  local -- "yes" --> done["Stop: files ready locally"]
  local -- "no" --> upload["Add-PnPFile to Scan Exception Reports"]
  upload --> list["Add-PnPListItem per exception, skip existing"]
  list --> mark["rpt.usp_MarkExceptionsPublished via dbo.IdList TVP"]
```

**Status**Run with -LocalOnly. Upload and provisioning have not been run against a tenant

### Exception feedback loop

Cycle

Rejected scans reach supervisors, who fix the cause. The Power BI `Exception Rate` measure shows whether the loop is working.

docs/10-sharepoint-and-sops.md · sharepoint/sop/SOP-101

```mermaid
%%{init: {'theme':'neutral'}}%%
flowchart LR
  scan["Operator scans"] --> reject["Service rejects, row in dbo.ScanExceptions"]
  reject --> publish["06:00 daily job"]
  publish --> sp["SharePoint report and worklist"]
  sp --> sup["Supervisor triages, sets Resolution"]
  sup --> fix["Re-tag, retrain, fix the scanner"]
  fix --> scan
```

## Dev and ops running it locally

### Simulator modes

Flowchart

`backfill` writes straight to SQL through the same stored procedures. The live modes use the real network path.

docs/07-simulator.md · src/YardTracker.Simulator

```mermaid
%%{init: {'theme':'neutral'}}%%
flowchart LR
  backfill["backfill: 60 days of history"] --> db[("SQL Server")]
  traffic["traffic: handheld scans, misreads, retries"] --> wcf["WCF service"]
  trucks["trucks: deliveries with GPS"] --> wcf
  plc["plc: Modbus server on 5020"] --> gw["Edge gateway"] --> wcf
  wcf --> db
  db --> reports["SSRS, Power BI, SharePoint"]
```

### Build and run order

Flowchart

The quick-start sequence from the README. Optional live feeds each run in their own terminal once the service is up.

README.md · docs/11-build-run-test.md

```mermaid
%%{init: {'theme':'neutral'}}%%
flowchart TD
  a["1. scripts/Deploy-Database.ps1 -Force"] --> b["2. dotnet build YardTracker.slnx"]
  b --> c["3. Simulator backfill for 60 days, then ETL"]
  c --> d["4. Start YardTracker.Service.exe"]
  d --> e["5. Start station HH-01, sign in with 1001"]
  d --> f["Optional: Simulator traffic"]
  d --> g["Optional: Simulator trucks"]
  d --> h["Optional: Simulator plc, then EdgeGateway"]
  d --> t["dotnet test: contract, database and Modbus tests"]
```

Drawn from the repository's docs/ folder and source tree. 15 diagrams: 4 system views, 5 floor workflows, 4 back-office flows, 2 dev and ops flows.