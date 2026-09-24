# 11. Building, running, testing and deploying

## Prerequisites

| Requirement | Why |
|---|---|
| Windows | WPF, WCF on .NET Framework, LocalDB, PnP PowerShell |
| .NET 10 SDK | Station, client, gateway, simulator, Modbus, tests |
| .NET Framework 4.8 **targeting pack** | The WCF service. The runtime is built into Windows 11; the targeting pack comes with the Visual Studio "Managed desktop" workload |
| SQL Server or SQL Server Express **LocalDB** | The database. LocalDB is enough for everything except deploying SSRS |
| *Optional* SQL Server Integration Services | Only for `Run-Etl.ps1 -UseSsis` |
| *Optional* Power BI Desktop | Only to open the PBIP |
| *Optional* PowerShell 7.4+ and PnP.PowerShell | Only to publish to a real SharePoint tenant |

## Quick start

```powershell
# 1. Create the database and run every script in database/ in order
.\scripts\Deploy-Database.ps1 -Force

# 2. Build everything
dotnet build YardTracker.slnx

# 3. Generate 60 days of realistic history, then run the ETL once
dotnet run --project src\YardTracker.Simulator -- backfill --days 60

# 4. Start the WCF service (keep this window open)
.\src\YardTracker.Service\bin\Debug\net48\YardTracker.Service.exe

# 5. Start a scanning station; sign in with 1001, 2001 or 3001
dotnet run --project src\YardTracker.Station -- --station HH-01
```

Step 4 runs the built executable rather than `dotnet run` because a .NET Framework console app
needs its `App.config` beside it as `YardTracker.Service.exe.config`, which is what the build
produces.

### Optional live data, each in its own terminal

```powershell
dotnet run --project src\YardTracker.Simulator -- traffic --rate 1      # handheld scans, misreads, retries
dotnet run --project src\YardTracker.Simulator -- trucks --trucks 2     # deliveries with GPS (Fleet map tab)
dotnet run --project src\YardTracker.Simulator -- plc --port 5020       # Modbus TCP PLC simulator
dotnet run --project src\YardTracker.EdgeGateway                        # polls the PLC (Machines tab)
.\scripts\Run-Etl.ps1                                                   # incremental ETL
.\sharepoint\Publish-ScanExceptionReport.ps1 -LocalOnly                 # daily report to .\out\sharepoint
```

## `Deploy-Database.ps1` in detail

```powershell
.\scripts\Deploy-Database.ps1 [-Server '(localdb)\MSSQLLocalDB'] [-Database YardTracker] [-Force]
```

What it does, and why each part is there:

- Uses the `System.Data.SqlClient` built into Windows PowerShell — **no `sqlcmd` or SSMS
  required**, so the repository runs on a clean machine with only the SDK installed.
- Splits scripts on lines containing only `GO`, exactly as `sqlcmd` does, because `GO` is a batch
  separator understood by the tool, not by SQL Server. The regex is multiline and case-insensitive.
- Subscribes to `InfoMessage` so `PRINT` output from the scripts appears.
- Wraps each batch in try/catch and reports **which file, which batch number and the batch's first
  line** on failure. A bare "incorrect syntax near" over 40 KB of SQL is useless.
- Refuses to run against an existing database unless `-Force`, which then drops it with
  `SET SINGLE_USER WITH ROLLBACK IMMEDIATE` so an open connection cannot block the deployment.
- Enables `READ_COMMITTED_SNAPSHOT`, so readers do not block writers. On a system where reports,
  the station's inventory tab and the ETL all read while handhelds are scanning, this materially
  reduces blocking — and it is why the explicit `UPDLOCK, HOLDLOCK` in `usp_RecordScan` is written
  explicitly rather than relying on isolation level.
- Runs `database/*.sql` in **name order**, which is why they are numbered.

## Running the tests

```powershell
dotnet test tests\YardTracker.Tests
```

42 tests, all of which must pass.

| Group | What it proves |
|---|---|
| **Modbus** (`ModbusTests.cs`) | Byte-exact request layout against the spec, big-endian parsing, header round-trip, exception responses raising the device's code, a byte count that disagrees with the request being rejected, quantity bounds, two's-complement negative temperatures, an unknown state decoding to `Offline`, a real client and server talking over TCP, an unknown unit id returning `0x0B`, reading past the bank returning `0x02` **while keeping the connection open**, and an unreachable device timing out |
| **Parsing and contracts** (`ContractTests.cs`) | Scanner input classification across barcode, EPC, `LOC:`, `BADGE:` and junk; `NormalizeTag` refusing a location label; and by reflection: every operation declares the fault contract, wire names drop the `Async` suffix, the scan operations are named after the yard actions, and connectivity failures are distinguished from faults |
| **Database** (`DatabaseTests.cs`) | Idempotent retry, the item state machine, rejected scans being logged, moves staying on site and changing location, deliveries putting items `InTransit` and unloading only at the destination, **incremental ETL rebuilding days touched by late-arriving scans**, overdue grading, and telemetry for unknown machines being dropped |

### How the database tests work

`DatabaseFixture` creates a throwaway `YardTracker_Test` LocalDB database and runs the **real**
`database/*.sql` files into it — found by walking up from the test output directory until a folder
containing `01_schema.sql` appears. On dispose it clears the connection pools and drops the
database.

This is the important design choice: the tests exercise the scripts that will be deployed, not a
hand-maintained copy. A schema change that breaks a procedure fails the test run immediately.

If LocalDB is not installed, the fixture records a skip reason and the database tests **skip**
rather than fail, so the Modbus and contract tests still run on a machine without SQL Server.

## Deployment notes

### The WCF service

- Opens TCP **8523** (the three service endpoints) and **8524** (metadata).
- For handhelds on other machines: open the firewall for both ports, and use certificate or domain
  credentials. The comments in `App.config` point at the settings; the client side must match in
  `station.json` or `appsettings.json`.
- For production, wrap the same `ServiceHost` in a Windows Service, or host in IIS/WAS with
  `net.tcp` activation. Nothing in the service code changes.
- Grant the service account `EXECUTE` on the `dbo`, `logistics` and `telemetry` schemas — and no
  table permissions at all. Because every operation is a stored procedure, that is sufficient.

### The station

One build, deployed everywhere; only `station.json` differs per device. Or override on the command
line with `--station` and `--service`.

### The edge gateway

`builder.Services.AddWindowsService(...)` is already wired, so:

```powershell
sc.exe create "YardTracker Edge Gateway" binPath= "C:\path\YardTracker.EdgeGateway.exe"
```

Point `Modbus.Host` and `Modbus.Port` at the real PLC network (port 502) and set the poll interval
to suit the machines.

### SSRS

LocalDB is per-user and unreachable by the Report Server service. Retarget `YardTracker.rds` at a
full SQL Server instance before deploying the project.

### SSIS

The package uses the Microsoft OLE DB Driver 19 (`MSOLEDBSQL19`). Add it to a new Integration
Services project with **Add Existing Package**, or run it with `.\scripts\Run-Etl.ps1 -UseSsis`.
For production, deploy to SSISDB with environments and schedule it with SQL Agent, alerting on
`etl.RunLog` failures.

### Power BI

Open `powerbi/YardTracker.pbip`. The `SqlServer` and `SqlDatabase` parameters point at LocalDB by
default. The map visual needs map visuals enabled in **Options → Security**.

### SharePoint

See [10-sharepoint-and-sops.md](10-sharepoint-and-sops.md). Provision once, then schedule the
publisher daily after the ETL.

## Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| `Database 'YardTracker' already exists` | Safety check | Re-run with `-Force` |
| The service exits with a connection error at startup | `Db.Describe()` ran before `host.Open()` — by design | Check the connection string in `YardTracker.Service.exe.config` and that LocalDB is running (`sqllocaldb start MSSQLLocalDB`) |
| The station shows "Offline" with the service running | Binding mismatch | `Service.Security` in `station.json` must match the server binding. Try `--security None` on both sides for a quick test |
| "The database already contains items" from `backfill` | Refuses to layer history on history | `.\scripts\Deploy-Database.ps1 -Force` first |
| The gateway logs "Waiting for the YardTracker service" | Expected until the service is up | Start the service; the gateway retries every 5 s |
| The gateway logs a Modbus connection failure | The PLC simulator is not running | `dotnet run --project src\YardTracker.Simulator -- plc --port 5020` |
| Database tests are skipped | LocalDB missing | Install SQL Server Express LocalDB, or accept that only the unit tests run |
| `dtexec was not found` | SSIS not installed | Drop `-UseSsis`; the stored-procedure path does the same work |
| Reports show no data | The ETL has not run | `.\scripts\Run-Etl.ps1`, then check `etl.RunLog` |
| A `net48` build error about targeting packs | Targeting pack missing | Install the Visual Studio "Managed desktop" workload |

## Useful queries while demonstrating

```sql
-- The last few scans, decoded
SELECT TOP 20 * FROM dbo.vw_ScanEventDetails ORDER BY ScanEventId DESC;

-- Today's rejections
SELECT ExceptionType, COUNT(*) AS n
FROM dbo.ScanExceptions
WHERE OccurredAtUtc >= DATEADD(DAY, -1, SYSUTCDATETIME())
GROUP BY ExceptionType ORDER BY n DESC;

-- ETL health
SELECT TOP 10 RunId, RunSource, Status, StartedAtUtc, RowsExtracted, RowsWritten, AffectedDays, ErrorMessage
FROM etl.RunLog ORDER BY RunId DESC;

-- Where is every bundle from one heat, and who touched it?
SELECT * FROM dbo.vw_ItemDetails WHERE HeatNumber = 'K7781';
SELECT * FROM dbo.vw_ScanEventDetails WHERE TagId IN (SELECT TagId FROM dbo.Items WHERE HeatNumber = 'K7781')
ORDER BY ScannedAtUtc;

-- Latest telemetry per machine
SELECT * FROM telemetry.Machines AS m
CROSS APPLY (SELECT TOP 1 * FROM telemetry.MachineReadings AS r
             WHERE r.MachineId = m.MachineId ORDER BY r.RecordedAtUtc DESC) AS latest;
```

## A five-minute demo script

1. **Station** — sign in with badge 1001. In Receive mode, scan a new tag and show the item card.
   Move it with a `LOC:NYD-R01` scan. Check it out with a work order. Scan it again to show
   "Rejected: not allowed".
2. **Service console** — point out the per-operation log lines with timings, and the warning-level
   line for the rejection.
3. **Offline queue** — stop the service and scan twice. The header shows "Offline, 2 pending".
   Start the service and watch them sync, in order, with their original timestamps.
4. **Fleet map** — with `trucks` running, show a GPS trail being drawn.
5. **Machines** — with `plc` and the gateway running, show live temperatures and a fault appearing.
6. **Reports** — run `Run-Etl.ps1`, then show the SSRS overdue report and throughput chart, and the
   Power BI Flow & Bottlenecks page with the threading bay standing out.

Next: [12-end-to-end-walkthroughs.md](12-end-to-end-walkthroughs.md)
