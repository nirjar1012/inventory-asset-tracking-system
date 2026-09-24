# 9. Reporting: SSRS and Power BI

Two reporting tools, deliberately, because they answer different questions for different people.

| | SSRS | Power BI |
|---|---|---|
| Audience | Supervisors, shipping office | Plant and operations managers |
| Question | "What do I do today?" | "How are we doing, and where is the constraint?" |
| Style | Parameterised, tabular, **printable** | Interactive, cross-filtered, exploratory |
| Reads | `rpt.usp_*` dataset procedures | `rpt.vw_*` star-schema views |
| Delivery | Report server, subscription, PDF on a clipboard | Desktop or workspace |

Both read only from the `rpt` schema. That is the contract: OLTP tables can be reshaped without
breaking a single report.

## SSRS — operational reports

`reports/ssrs/YardTracker.Reports`, RDL 2016.

All three reports share `YardTracker.rds`, and all three take a **site parameter** driven by
`rpt.usp_SiteList`, which returns a synthetic `ALL` row first — so "(All sites)" is a real
selectable value rather than a null special case that every query has to handle.

### Current Inventory by Location

**Question:** where is everything right now, and which locations are full?

From `rpt.usp_CurrentInventoryByLocation`. Grouped by site, then location. Per location: item
count, pieces, tons, capacity, utilization percentage and the age of the oldest item.

Utilization is conditionally formatted — amber over 85 %, red over 100 %. Over 100 % is a real
state, not a bug: it means more steel is on that pad than it was rated for, which is a safety and
access problem a supervisor needs to see, so the report shows it rather than clamping it.

The procedure uses a `LEFT JOIN` from `Locations` to `Items`, so an **empty** location still
appears with zeros. A location that has been cleared out is information too.

### Items Overdue for Movement

**Question:** what capital is sitting still?

From `rpt.usp_ItemsOverdueForMovement`, with a `@DaysThreshold` parameter (default 30). Lists tag,
product, grade, heat number, pieces, tons, last-moved date **in the site's local time**, and days
idle. Sorted by days idle descending, so the worst offenders are at the top of page one.

Severity is graded: `Overdue` at the threshold, `Critical` at twice it. That gives the report two
levels of urgency from one parameter, so the same report serves a weekly review and an escalation.

This is the report that turns into money: aged stock means tied-up capital, surface rust and
re-inspection cost.

### Daily Yard Throughput

**Question:** how much moved, and is the trend going the right way?

From `rpt.usp_DailyThroughput` over a date range. Receipts, check-ins, check-outs, moves, truck
loads, truck deliveries, total scans, and inbound and outbound tons per day, with a chart.

The procedure left-joins from `rpt.DimDate`, so days with **no** activity appear as zero rather
than vanishing. A gap in a trend line is ambiguous; a zero is a fact. Weekends are flagged with
`IsWeekend` so the reader can tell a quiet Sunday from a bad Tuesday.

### Deployment note

`YardTracker.rds` points at LocalDB by default. LocalDB instances are **per Windows user** and are
not reachable by the Report Server service account, so before deploying, retarget the data source
at a full SQL Server instance hosting `YardTracker`.

**Status:** all three render to PDF with the ReportViewer engine against live data. Not yet
deployed to a report server.

## Power BI — the executive model

`powerbi/` is a **PBIP** (Power BI Project): the model and report stored as text — TMDL for the
semantic model, PBIR JSON for the report — instead of a binary `.pbix`. That means both are
diffable, reviewable and mergeable like source code. It is a meaningful choice: a `.pbix` is
opaque to code review, so model changes normally arrive as an unreviewable blob.

### Parameters

`expressions.tmdl` defines two parameters, `SqlServer` and `SqlDatabase`, defaulting to
`(localdb)\MSSQLLocalDB` and `YardTracker`. Every table's M query is
`Sql.Database(SqlServer, SqlDatabase)`, so retargeting the whole model at another server is two
values in **Transform data → Edit parameters**, not eleven query edits.

### The star schema

Eleven tables in import mode, each from one `rpt` object.

**Dimensions**

| Table | Source |
|---|---|
| `Date` | `rpt.DimDate` |
| `Location` | `rpt.vw_DimLocation` |
| `Product` | `rpt.vw_DimProduct` |
| `Machine` | `rpt.vw_DimMachine` |

**Facts**

| Table | Source | Grain |
|---|---|---|
| `Daily Movement` | `rpt.vw_FactDailyMovement` | Business day × location × category |
| `Current Inventory` | `rpt.vw_CurrentInventory` | One row per item, current state |
| `Location Stays` | `rpt.vw_LocationStays` | One row per stay of an item at a location |
| `Machine Hourly` | `rpt.vw_MachineReadingsHourly` | Machine × hour |
| `Deliveries` | `rpt.vw_Deliveries` | One row per delivery |
| `GPS Pings` | `rpt.vw_GpsPings` | One row per position report |
| `Scan Exceptions` | `rpt.vw_ScanExceptions` | One row per rejected scan |

Thirteen relationships, all fact-to-dimension single-direction. `Date` joins on integer `DateKey`
rather than a datetime, which is both faster and unambiguous. Note that `Date` is the **plant
business day**, converted with `AT TIME ZONE` in the views — so a 9 pm Houston scan lands on the
right day in a slicer.

Because every fact joins the same `Date` and `Location`, a site slicer or a date range filters all
six facts together. That is what makes "the threading bay in week 37" a single interaction.

### The 33 measures

Grouped into display folders. The ones worth understanding:

| Measure | Definition | Why it is written this way |
|---|---|---|
| `Utilization %` | `DIVIDE([Tons On Hand], [Capacity Tons])` | `DIVIDE` rather than `/` so a location with no capacity gives blank, not an error |
| `Scans` | Sum of all six event-count columns | `MovesIn` only, not `MovesOut`, so a move is counted once |
| `Scans 7-Day Avg` | `AVERAGEX(DATESINPERIOD(...), [Scans])` | Smooths the weekly cycle so the trend is readable |
| `Avg Temperature C` | `DIVIDE(SUMX(Hourly, Avg × Samples), SUM(Samples))` | **Sample-weighted.** Averaging hourly averages would over-weight an hour with two readings against one with twelve |
| `Running %`, `Fault Time %` | Same weighting over `RunningPct` and `FaultSamples` | Machine availability, correctly weighted |
| `Hours Over Warning` | `COUNTROWS(FILTER('Machine Hourly', [MaxTemperatureC] >= RELATED(Machine[TempWarnC])))` | Compares each machine against **its own** limit via `RELATED`. A single global threshold would be meaningless across a 232 °C oven and a 64 °C crane |
| `Avg Dwell Hours` | Average `DwellHours` where `IsOpen = FALSE` | Only completed stays. Including open stays would drag the average toward "however long the report has been running" |
| `P90 Dwell Hours` | `PERCENTILEX.INC` over closed stays | The tail is the bottleneck signal. An average hides a queue; the 90th percentile does not |
| `Process Dwell Hours` | `Avg Dwell Hours` with `KEEPFILTERS(Location[LocationType] IN {"Dock","Bay","Staging","Quarantine"})` | Restricts to locations where waiting is *bad*. Storage racks are supposed to hold material |
| `Exception Rate` | `DIVIDE([Exception Count], [Scans] + [Exception Count])` | Rejections as a share of **all** scan attempts, which is the honest denominator |
| `Items Overdue 30d`, `Avg Days Since Move` | Over `Current Inventory` | The aging story |

### The three report pages

**Yard Overview** — the state of the yard now.

Six KPI cards (items in yard, tons on hand, utilization, overdue, average days idle, open
exceptions), a scans trend with the 7-day average, stock aging by bucket, tons by product
category, and utilization by location. A site slicer filters everything.

**Flow & Bottlenecks** — where material is getting stuck.

Movements by type, exceptions by type, process dwell, and a dwell table by location. This is the
page that pays for the `vw_LocationStays` view: with the simulator's data, the threading bay's
P90 dwell stands well above every other location, which is the plant's constraint made visible.

**Machines & Trucks** — the floor and the fleet.

Cards for deliveries, tons delivered, average transit and hours over warning; machine utilization;
a temperature trend; a map of truck positions; and a route table.

### Notes for opening it

- Open `powerbi/YardTracker.pbip` in Power BI Desktop.
- The parameters default to LocalDB. Change them if the database is elsewhere.
- The map visual needs map visuals enabled in **Options → Security**.

**Status:** the TMDL has been loaded and round-tripped with the Analysis Services `TmdlSerializer`
and the report JSON validated against the Fabric PBIR schemas. Not yet opened in Power BI Desktop.

## Why the split is the right one

An SSRS report is a **document**: parameterised, paginated, printable, subscribable, and it looks
the same every time — which is what you want for something a supervisor prints and carries into
the yard, or attaches to an audit.

A Power BI page is an **exploration**: cross-filtering, drill-down, a model that answers questions
nobody wrote a report for.

Trying to make one tool do both jobs produces a Power BI page nobody can print and an SSRS report
nobody can explore. Using both, over the same `rpt` layer, costs one extra schema and gives each
audience the right thing.

Next: [10-sharepoint-and-sops.md](10-sharepoint-and-sops.md)
