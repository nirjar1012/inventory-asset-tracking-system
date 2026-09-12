# Interview talking points

## The 60-second version

"I built a small version of what a pipe and steel yard runs on. Operators on wireless handhelds scan bundles in, out and between racks. Drivers scan loads onto trucks that report GPS. Machines on the plant floor report temperature and status over Modbus TCP.

The clients are WPF. They talk to a WCF service over net.tcp, and the service calls stored procedures in SQL Server. An SSIS package does an incremental load into a daily movement summary. SSRS covers the operational reports (what's where, what hasn't moved, daily throughput), and Power BI gives the executive view: utilization, bottlenecks, machine uptime. Rejected scans get published to SharePoint every morning.

The part I'm proudest of is how it handles an unreliable wireless network without losing or double counting a scan, all the way from the handheld to the ETL."

## Design decisions worth explaining

### 1. Idempotent scans (`ClientScanId`)
- The handheld generates a GUID when the tag is read. If the reply is lost on Wi-Fi and the device retries, the stored procedure finds the GUID and returns the original result (`Duplicate`) without moving the item twice.
- A unique constraint backs it up, so two retries racing each other are also safe.
- **Why it matters:** "at-least-once delivery plus idempotent receiver" is the practical way to get exactly-once behavior on a lossy network.

### 2. Offline queue on the device
- Connectivity failures (not faults) put the scan into a JSON file on disk, written atomically. Scans replay in order every 10 seconds.
- New scans queue behind older ones, so a Move is never applied before the Receive it depends on.
- The original device timestamp is kept, so history shows when the steel actually moved.

### 3. Business outcome vs fault
- "That item is already checked out" is a normal event on a shop floor. It comes back as a `ScanResult` with `Outcome = Rejected` and is logged for supervisors.
- Faults (`FaultException<ServiceFault>`) are reserved for bad requests and infrastructure problems, and carry a correlation id instead of a stack trace.
- **Why it matters:** clients don't need try/catch for normal flow, and the exception log becomes useful data (exception rate, misread hot spots).

### 4. Business rules in stored procedures, with explicit locking
- `UPDLOCK, HOLDLOCK` on the item row makes concurrent scans of the same bundle serialize: no lost updates, no deadlock-prone read-then-write.
- `SET XACT_ABORT ON` and `TRY/CATCH` with `XACT_STATE()` keep the transaction handling correct.
- The WCF layer stays thin: validation, mapping, logging.
- **Trade-off to acknowledge:** logic in T-SQL is harder to unit test, which is why the integration tests deploy the real scripts to LocalDB.

### 5. Incremental ETL that survives late data
- **Watermark:** `rowversion`, not `ScannedAtUtc`. Device time is not arrival order.
- **Upper bound:** `MIN_ACTIVE_ROWVERSION() - 1`, so a long-running transaction can't commit rows behind the watermark.
- **Merge:** rebuild only the (business day, location) cells the new events touch, from the full log, with delete + insert in one transaction. It is idempotent: rerunning gives the same answer, and a scan two days late fixes that day's totals.
- **Why not MERGE:** delete + insert of a well-defined slice is simpler to reason about and avoids MERGE's concurrency pitfalls.
- **Run control:** `etl.RunLog`, an applock against overlapping runs, and an OnError handler that marks the run failed.

### 6. Legacy server, modern client
- The service is classic WCF on .NET Framework 4.8, which is what most plants with WCF actually run. The station, gateway and simulator are .NET 10 using the dotnet/wcf client libraries, sharing one multi-targeted contracts assembly.
- **Migration story:** clients can move to modern .NET first without touching the server. The server can later move to CoreWCF with the same contracts, or to gRPC behind the same service interfaces.

### 7. WCF client hygiene
- `ChannelFactory` is cached per contract, with a new channel per call.
- A faulted channel is aborted, never closed or reused. This is the most common WCF client bug.
- Short open/send timeouts so a handheld fails fast and queues instead of hanging.
- The metadata endpoint gets its own port, because endpoints sharing a port must have identical TCP listener settings.

### 8. Modbus TCP from the spec
- MBAP header (transaction id, protocol id, length, unit id) + PDU; FC03 read holding registers, FC06 write single register; big-endian.
- One TCP endpoint, several unit ids: the way a Modbus TCP-to-RTU gateway fronts serial PLCs.
- **Register map:** temperature as int16 tenths (two's complement for negatives), 32-bit counters split across two registers, fault reset as a writable register.
- **Gateway:**
  - Loads the machine list from the database, so a new machine is a data change.
  - Polls on a fixed interval and logs state changes rather than every sample.
  - Buffers readings with their original timestamps while the service is down (store and forward, the historian pattern).
  - A Modbus exception response is an application answer, so the TCP connection stays open. Socket errors drop and reconnect.

### 9. Traceability
- Every item carries a heat number from the mill test report, and every movement is an immutable event with operator, station and reference (work order, BOL, PO).
- For steel this matters for quality holds and recalls: you can answer "where is every bundle from heat K7781, and who touched it?"

### 10. Reporting split
- **SSRS** is operational and printable: parameters, grouping, conditional formatting (utilization over 85% / 100%, critical overdue).
- **Power BI** is exploratory and executive: star schema views, date table in plant time, measures like sample-weighted temperature and P90 dwell.
- **Bottleneck analysis:** dwell time at process locations (docks, bays, staging, QA hold) from `LEAD()` over the scan log. In the sample data, the threading bay stands out.

## Likely questions

**How would you scale to hundreds of handhelds?**
The service is stateless and per-call, so add hosts behind a TCP load balancer. Connection pooling and short transactions keep SQL contention low. The item row lock is the only serialization point, and it only blocks scans of the same bundle. Telemetry would move to batching into a separate database or a time-series store.

**What about RFID reads that fire 50 times a second at a portal?**
Debounce on the reader side (same EPC within N seconds = one read), then send one scan with one `ClientScanId`. The idempotency key handles anything that slips through.

**How do you secure it on a workgroup wireless network?**
`netTcpBinding` with transport security and certificate client credentials, or message security with username tokens against the Operators table. Also network segmentation (plant VLAN) and least-privilege SQL logins for the service account (EXECUTE on schemas only).

**Why not EF / an ORM?**
For a small set of well-defined transactional operations, stored procedures give explicit locking, one round trip per scan, and a clear permission boundary. An ORM would be fine for the admin screens.

**What happens if the ETL fails halfway?**
The merge is one transaction, so either the watermark advances with the data or neither happens. The failed run is marked in `etl.RunLog`, and the next run picks up from the old watermark.

**How do you test this?**
- xUnit: byte-exact Modbus frames and a loopback client/server.
- Scanner input parsing.
- WCF contract checks: every operation declares the fault, and wire names drop the `Async` suffix.
- Integration tests that deploy the real SQL scripts to a throwaway LocalDB database and exercise the state machine, idempotency, deliveries and the late-arriving-data ETL case.

## What I'd do next for production

- Host the service as a Windows Service with a health endpoint, plus structured logging (Serilog → Seq or Application Insights).
- Certificate-based transport security for non-domain handhelds.
- A supervisor screen to resolve scan exceptions, writing back from the SharePoint list.
- Deploy SSIS to SSISDB with environments, and schedule it through SQL Agent with alerting on `etl.RunLog` failures.
- Row-level security in Power BI by site.
- Real RFID reader integration (LLRP) behind the same `SimulatedRfidReader` abstraction.

## 5-minute demo script

1. **Station:** sign in with badge 1001. In Receive mode, scan a new tag and show the item card. Then Move with a `LOC:NYD-R01` scan, Check out with a work order, and scan it again to show "Rejected: not allowed".
2. **Service console:** point out the per-operation log lines.
3. **Offline queue:** stop the service and scan twice. The header shows "Offline, 2 pending". Start the service and watch them sync.
4. **Fleet map:** with `trucks` running, show a GPS trail being drawn.
5. **Machines tab:** with `plc` + gateway running, show live temperatures and a fault appearing.
6. **Reports:** run `Run-Etl.ps1`, then show the SSRS reports (overdue report, throughput chart) and the Power BI Flow & Bottlenecks page.
