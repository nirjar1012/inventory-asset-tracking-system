# 12. End-to-end walkthroughs

The earlier chapters describe each component. This one traces four journeys **through** them, so
you can see how the pieces actually interact — and where each design decision earns its keep.

---

## Walkthrough 1: one scan, from tag to dashboard

An operator moves a bundle of casing from the receiving dock to rack 3.

### On the device

**1. The scanner fires.** The operator points the handheld at the rack end cap. The
keyboard-wedge scanner types `LOC:NYD-R03` plus Enter into whatever has focus. If focus was on a
mode tile rather than the scan box, `ScanView.OnPreviewTextInput` reroutes the characters into the
scan box — the read is not lost.

**2. `ScanCodes.Parse`** cleans the text (strip control characters, trim, upper-case) and
classifies it as `Location`. The station switches the target location and shows a blue banner.
**No server call.**

**3. The operator scans the bundle tag,** `YT-004211`. This parses as `ItemTag`.

**4. `ScanViewModel.RecordAsync`** builds a `ScanRequest`:

```csharp
ClientScanId = Guid.NewGuid(),   // the idempotency key, born here
TagId        = "YT-004211",
LocationCode = "NYD-R03",
OperatorBadge= "1001",
StationCode  = "HH-01",
ScannedAtUtc = DateTime.UtcNow,  // the device clock
```

An entry appears in the session log immediately, marked pending.

**5. `ScanSubmitter.SubmitAsync`.** The queue is empty, so it sends directly.

### Over the wire

**6. `YardTrackerClient.InventoryAsync`** takes the cached `ChannelFactory<IInventoryService>`,
creates a **new channel**, calls `MoveItemAsync`, then closes it — or aborts it if anything threw.

**7. `netTcpBinding`** frames the message as binary over TCP 8523, signed and encrypted with
Windows transport security.

### In the service

**8. `YardTrackerService.MoveItemAsync`** wraps the body in `Operation.Run`, which starts a
stopwatch and installs the exception-to-fault translation.

**9. `Guard` validates:** the tag is non-empty and within length, the badge and station codes are
present, the `ClientScanId` is not `Guid.Empty`, the timestamp is normalised to UTC.

**10. `Db.QuerySingleAsync("dbo.usp_MoveItem", ...)`** with typed parameters.

### In the database

**11. `usp_MoveItem`** delegates to `usp_RecordScan` with `@Action = 'Move'`.

**12. Idempotency check.** `ClientScanId` is not in `ScanEvents` and not in `ScanExceptions`.
This is a new scan.

**13. Resolve.** Operator 1001 is active. Station HH-01 is registered. `NYD-R03` is an active
location. The device clock is not more than five minutes ahead.

**14. Lock and transition.**

```sql
SELECT ... FROM dbo.Items WITH (UPDLOCK, HOLDLOCK) WHERE TagId = 'YT-004211'
```

The item is `InYard` at `NYD-DOCK1`, same site, different location. The move is legal.

**15. Apply, in one transaction.** `Items.CurrentLocationId` becomes rack 3;
`LastMovedAtUtc` advances (but only if the scan is newer than what is stored, so a late scan
cannot make fresh stock look stale); a `ScanEvents` row is inserted with both locations, the
operator, the station, and the pieces and weight snapshotted. `RowVer` is assigned at commit.

**16. Return.** Outcome `Accepted`, a message, the new event id, and the full item card joined
from `vw_ItemDetails` — so no second round trip is needed.

### Back on the device

**17.** The service logs one line: `MoveItem  HH-01 YT-004211 -> Accepted (7 ms)`.

**18.** The station shows a green banner, "Moved NYD-DOCK1 → NYD-R03.", updates the item card and
the session log, and re-focuses the scan box for the next read.

### Into reporting

**19.** Within 15 minutes the ETL runs. `etl.usp_BeginRun` takes the applock, reads the watermark,
and sets the upper bound to `MIN_ACTIVE_ROWVERSION() - 1`. This event's `RowVer` falls in range.

**20.** The extract stages it. The merge notes that **two** cells are touched — (today,
`NYD-DOCK1`) and (today, `NYD-R03`) — deletes them and recomputes both from the full log. The
event counts as `MovesOut` at the dock and `MovesIn` at the rack.

**21.** The watermark advances and `etl.RunLog` records `Succeeded` in the same transaction.

**22.** SSRS shows the bundle at rack 3 with a reset idle clock. Power BI's throughput trend ticks
up by one; `vw_LocationStays` closes the dock stay and opens a rack stay.

**Elapsed from tag to database: a few milliseconds. From tag to dashboard: one ETL cycle.**

---

## Walkthrough 2: the same scan, with the Wi-Fi down

Same bundle, same move, but the operator is behind a stack of pipe with no signal.

**Steps 1-5 are identical.** The `ClientScanId` and device timestamp are already fixed.

**6'. The call fails.** `SocketException` or `TimeoutException` after the 5-second open timeout.

**7'. `YardTrackerClient.IsConnectivityFailure` returns `true`** — this is not a `FaultException`,
so the service never saw the request.

**8'. `ScanSubmitter` enqueues.** `OfflineScanQueue.Enqueue` appends and saves atomically:
write `offline-queue-HH-01.json.tmp`, then `File.Move` over the original. A dead battery here
loses nothing.

**9'. `SetOnline(false)`** fires `ConnectivityChanged`. The header changes to
**"Offline, 1 pending"**.

**10'. The operator keeps working.** Every subsequent scan goes straight into the queue, because
`SubmitAsync` sees `queue.Count > 0` and queues rather than attempting to send. **Order is
preserved** — which is what prevents a `Move` from being applied before the `Receive` that
created the item.

**11'. Twenty minutes later** the operator walks back into coverage. The `OfflineRetrySeconds`
timer calls `FlushAsync`, which the `_flushGate` protects from overlapping ticks.

**12'. Replay, in order.** Each scan is sent with its **original** `ClientScanId` and **original**
`ScannedAtUtc`. The service records them with those device timestamps and stamps its own
`ReceivedAtUtc`. The gap between the two columns is the outage, visible in the data.

**13'. Suppose one reply is lost on the way back.** The scan stays queued and is sent again. The
server finds the `ClientScanId` already in `ScanEvents` and returns `Duplicate` — nothing moves
twice. The station shows an amber "Already recorded" banner, which the SOP tells the operator to
ignore.

**14'. The ETL handles the late data.** The scans are 20 minutes old, possibly across a business-day
boundary. Because the watermark is `rowversion` — commit order, not device time — they are in
range. The merge rebuilds the cells for **the day they actually happened**, which may be an older
day than today. `etl.RunLog.AffectedDays` shows more than 1.

**Nothing is lost. Nothing is double counted. History says when the steel actually moved.**

---

## Walkthrough 3: a truck delivery between sites

Three bundles go from North Yard to the Coating & Threading Plant.

**1. Loading.** The driver signs in with badge 3001 on `HH-DRV1` and scans three tags onto
`TRK-101`. The client sends one `StartDeliveryRequest` with a `ClientRequestId` and all three tags.

**2. `logistics.usp_StartDelivery` validates the whole load atomically.** It takes
`UPDLOCK, HOLDLOCK` on all three items at once and builds a single problem list with `STRING_AGG`:

- any tag not registered
- any item not `InYard`
- any item not at the origin site
- the truck already being on an active delivery
- total weight over `MaxPayloadLbs`

If anything fails, **nothing** happens — a half-loaded truck is not a state the system can be in.
The unique filtered index `UX_Deliveries_ActivePerTruck` backs up the "one active delivery per
truck" rule at the database level, so even a race cannot produce two.

**3. On success:** a `Deliveries` row with status `InTransit`, three `DeliveryItems`, all three
items set to `InTransit` with `CurrentLocationId = NULL` (the check constraint requires it), and
three `LoadTruck` scan events.

**4. On the road.** The driver handheld calls `ReportPositionAsync` every few seconds.
`usp_RecordGpsPing` attaches each ping to the truck's **active** delivery, so the breadcrumbs are
associated with a specific load. The station's Fleet map polls `GetFleetStatusAsync` and draws the
trail.

**5. Arrival.** `CompleteDeliveryAsync` with the destination location. The procedure enforces that
the arrival location belongs to the **destination** site — you cannot unload a North-Yard-bound
load at the plant. All three items become `InYard` at the arrival location, the delivery is marked
`Delivered` with `ArrivedAtUtc`, and three `Deliver` events are written.

**6. Reporting.** `rpt.vw_Deliveries` computes `TransitMinutes`, item count and tonnage. The ETL
counts `TruckLoads` at the origin and `TruckDeliveries` at the destination. Power BI's Machines &
Trucks page shows delivery count, tons delivered, average transit and the route table.

The state machine held throughout: `InYard → InTransit → InYard`, with the site rule enforced at
both ends.

---

## Walkthrough 4: a machine faults

The CNC threader trips at 02:14.

**1. The PLC changes state.** In the simulator, `MachineModel.Step` rolls a fault against the
threader's 40-hour MTBF, sets `State = Fault` and picks fault code 402. `Publish()` encodes the
snapshot into registers 40001-40005.

**2. The gateway polls.** Five seconds later, FC03 unit 4, start 0, quantity 5. On the wire:

```
00 2A  00 00  00 06  04  03  00 00  00 05      request
00 2A  00 00  00 0D  04  03  0A  02 26 00 03 ...  response
```

**3. Decode.** `MachineRegisterMap.Decode` reads `0x0226` = 550 tenths = 55.0 °C, state 3 =
`Fault`, the 32-bit cycle count, and fault code 402.

**4. Log the change, not the sample.** `_lastState["THR-C1"]` was `Running`, so the gateway logs
one **warning**: `THR-C1 is Fault (fault 402) at 55.0 C`. The next 200 polls while it stays
faulted log nothing — the state has not changed.

**5. Forward.** The reading is buffered and sent with the next batch through
`RecordReadingsAsync`. `telemetry.usp_RecordMachineReadings` inserts it via the TVP, joined to
`Machines` and `MachineStatuses`.

**6. If the WCF service happens to be down,** the reading stays in the buffer with its original
timestamp and is sent when the service returns. The fault is recorded at 02:14, not at whenever
the service restarted.

**7. On the station.** The Machines tab polls `GetMachineStatusAsync`. The THR-C1 tile turns red,
showing `Fault`, `Code 402`, 55.0 °C and "Updated 3s ago". Its temperature bar is scaled against
the threader's own 65/80 °C limits, so it reads correctly next to the 232 °C oven.

**8. In Power BI.** `rpt.vw_MachineReadingsHourly` rolls the hour up with a sample count.
`Fault Time %` rises for that hour; `Running %` falls. Both are sample-weighted, so an hour with
twelve readings does not count the same as one with two.

**9. Clearing the fault.** With `AutoResetFaultsAfterPolls > 0`, the gateway writes 1 to register
40011 with FC06 after N faulted polls. The simulated device accepts the write **only** at that
address — the status registers reject it with `IllegalDataAddress` — clears the fault, and the
next poll shows the machine recovered. In production a human does this; the default is off.

**10. The business consequence.** The threader is the plant's constraint. While it is down,
casing and tubing queue at the threading bay. `vw_LocationStays` records those stays;
`P90 Dwell Hours` for `CTP-BAY2` rises. The bottleneck shows up in the data before anyone files
a report about it.

---

## What the four walkthroughs have in common

Every one of them passes through the same four ideas:

| Idea | Walkthrough 1 | 2 | 3 | 4 |
|---|---|---|---|---|
| **Idempotency key generated at the edge** | `ClientScanId` | replayed unchanged, returns `Duplicate` | `ClientRequestId` on the delivery | timestamped at the reading, not at the send |
| **Store and forward on failure** | not needed | offline queue | queued like any scan | gateway buffer |
| **Business outcome, not exception** | `Accepted` | `Duplicate` | `Rejected` with the whole problem list | Modbus exception keeps the connection open |
| **The event log is the truth** | `ScanEvents` | replayed with device time | `LoadTruck` and `Deliver` events | `MachineReadings` |

Next: [13-glossary.md](13-glossary.md)
