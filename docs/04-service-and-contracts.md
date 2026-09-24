# 4. The service, the contracts and the client

Three projects make up the middle tier: `YardTracker.Contracts` (the shared vocabulary),
`YardTracker.Service` (the host), and `YardTracker.Client` (the channel plumbing every
.NET client uses).

## `YardTracker.Contracts`

Multi-targeted `net48` **and** `net10.0` from one source set. The `net48` build is referenced
by the WCF host (classic `System.ServiceModel` from the GAC); the `net10.0` build is referenced
by the station, gateway and simulator (the `System.ServiceModel.Primitives` NuGet package).
Contract drift between server and client is therefore structurally impossible.

Namespaces are versioned and explicit:

```csharp
public const string Service = "urn:yardtracker:services:2026-09";
public const string Data    = "urn:yardtracker:data:2026-09";
```

Dated namespaces mean a future breaking change ships as a new namespace that old clients
simply do not match, instead of silently deserialising wrong.

### Three service contracts, split by consumer

| Contract | Consumer | Operations |
|---|---|---|
| `IInventoryService` | Scanning stations: handhelds, RFID portals, kiosks | `SignIn`, `GetLocations`, `GetProducts`, `LookupItem`, `SearchItems`, `ReceiveItem`, `CheckInItem`, `CheckOutItem`, `MoveItem`, `GetItemHistory`, `GetRecentScans`, `GetInventoryByLocation`, `GetOpenExceptions` |
| `ILogisticsService` | Driver handhelds and the fleet map | `GetSites`, `StartDelivery`, `CompleteDelivery`, `ReportPosition`, `GetFleetStatus`, `GetDeliveryRoute` |
| `ITelemetryService` | The Modbus edge gateway | `GetMachines`, `RecordReadings`, `GetMachineStatus` |

The split is by consumer, not by table. A device gets a channel to exactly the surface it needs,
which is both a security boundary (a gateway cannot move inventory) and a deployment one — each
contract has its own endpoint address and could later be hosted separately.

Every single operation declares `[FaultContract(typeof(ServiceFault))]`. There is a
reflection test in `ContractTests.cs` asserting it, because forgetting one on a new operation
would silently turn a typed fault into an opaque `FaultException` at the client.

Operations are declared `Async` in C# but WCF strips the suffix on the wire (`CheckInItemAsync`
becomes `CheckInItem`). A second reflection test pins that, so the wire contract cannot be
changed by a rename refactor.

### Data contracts

About 25 `[DataContract]` types. The ones worth understanding:

**`ScanRequest`** — one check-in, check-out or move.

| Member | Why it is there |
|---|---|
| `ClientScanId` | Generated on the device when the tag is read. Retries reuse it. **This is the idempotency key** |
| `TagId` | The barcode or EPC |
| `LocationCode` | Target for check-in and move, ignored for check-out |
| `OperatorBadge`, `StationCode` | Who and where |
| `ScannedAtUtc` | The device clock at the moment of the read. May be well in the past for a scan queued offline |
| `Reference` | Work order, BOL or PO |

**`ScanResult`** — the answer.

`Outcome` is `Accepted`, `Duplicate` (the same `ClientScanId` was already processed, nothing
changed) or `Rejected`. On a rejection, `ExceptionType` names it and `Message` is
operator-readable. `Item` carries the full current item card, so the station renders the result
without a second round trip — on a handheld over wireless, saving a round trip per scan is the
difference between a responsive device and a frustrating one.

**`ServiceFault`** — `Code` (`Validation`, `Rejected`, `Unavailable`, `Internal`), `Message`,
and `CorrelationId` that matches the service log entry. No stack trace ever reaches a client.

**`ReceiveRequest`** carries the product code, heat number and piece count a brand-new tag needs.
**`MachineReading`** is the telemetry row. **`GpsPosition`** and **`TruckStatus`** drive the map.

### `ScanCodes` — interpreting what the scanner typed

A keyboard-wedge barcode scanner and an RFID reader both deliver the same thing a person typing
would: text, usually with a trailing CR/LF, sometimes with GS1 separators. `ScanCodes.Parse`
turns that into one of five kinds:

| Kind | Recognised as | Example |
|---|---|---|
| `Empty` | Nothing after cleaning | |
| `ItemTag` | A barcode label or a 96-bit RFID EPC (24 hex characters) | `YT-004211`, `E28011606000A1B2C3D4E5F6` |
| `Location` | The `LOC:` prefix, printed on rack end caps | `LOC:NYD-R01` |
| `Badge` | The `BADGE:` prefix, printed on ID cards | `BADGE:1001` |
| `Invalid` | Anything else | |

`Clean` strips control characters, trims and upper-cases before matching, so a scanner that
appends a carriage return or a tab behaves identically to one that does not.

The prefix scheme is what makes the station usable one-handed: an operator never has to put the
scanner down and touch the screen to change location — they scan the rack label instead.

## `YardTracker.Service` — the WCF host

### Hosting

`Program.cs` is deliberately small: create a `ServiceHost`, add the error-handler behaviour,
check the database connection **before** opening (so a misconfigured connection string fails at
startup with a clear message rather than on the first scan), open, log every endpoint, and wait
for Ctrl+C. Shutdown closes with a 10-second grace period; failure aborts.

Self-hosted in a console makes the demo visible — you can watch operations log as they happen.
The identical `ServiceHost` can be wrapped in a Windows Service or hosted in IIS/WAS with
`net.tcp` activation; nothing else changes.

### Service behaviour

```csharp
[ServiceBehavior(
    InstanceContextMode = InstanceContextMode.PerCall,
    ConcurrencyMode = ConcurrencyMode.Multiple,
    Namespace = ContractNamespaces.Service)]
public sealed class YardTrackerService : IInventoryService, ILogisticsService, ITelemetryService
```

**Per-call** instancing means a fresh instance per request and no server-side session state, so
the service is stateless: it scales with handheld count, survives a client dropping mid-call, and
can sit behind a load balancer without sticky routing. **Concurrency multiple** means requests
are not serialised through a lock — safe precisely because there is no instance state.

One class implements all three contracts. That is a hosting convenience (one process, one
config); the contracts stay separate so they can be split later without a client change.

### The operation wrapper

Every operation body is passed to `Operation.Run`, which does four things uniformly:

1. Times the call with a `Stopwatch`.
2. Logs one line: operation name, request detail, a short result summary, elapsed milliseconds.
   Rejections log at **warning** level, so an operator-visible problem stands out in the console.
3. Converts exceptions into typed faults:

   | Caught | Becomes | Why |
   |---|---|---|
   | `ServiceValidationException` | `Validation` fault | The caller must fix the request |
   | `SqlException` with number 50000-50999 | `Rejected` fault | A business rule raised with `THROW` in a procedure. The message is already operator-readable, so it is passed through |
   | Any other `SqlException` | `Unavailable` fault plus a correlation id | The database is down. The client is told to retry shortly; the details go to the log only |
   | Anything else | `Internal` fault plus a correlation id | Unexpected. The client gets a reference number, never a stack trace |

4. Returns the result.

That single wrapper is why the service code reads as a list of operations rather than a wall of
try/catch. `Guard` supplies the validation primitives (`Required`, `Optional`, `NotEmpty`,
`NotNull`, `Clamp`, and `Utc`, which normalises a default or local `DateTime` to UTC).

`UnhandledErrorLogger` is an `IServiceBehavior` plus `IErrorHandler` catching what happens
*outside* an operation — serialization failures, channel faults — which would otherwise vanish.

### Data access

`Data/Db.cs` is a thin ADO.NET wrapper, about 90 lines. Everything goes through a stored
procedure; there is no dynamic SQL anywhere. Three methods: `QueryAsync`, `QuerySingleAsync`,
`ExecuteAsync`, all `async` all the way down so threads are not blocked on I/O while a handheld
waits.

One subtlety worth pointing out — after reading rows, `QueryAsync` drains remaining result sets:

```csharp
while (await reader.NextResultAsync().ConfigureAwait(false)) { }
```

Without this, an error raised by a procedure *after* its first result set would be swallowed.

Typed parameter helpers (`AddVarChar`, `AddDateTime2`, `AddDecimal`, `AddTable` …) set explicit
SQL types, precision and scale. That is not cosmetic: implicit type inference is a classic source
of parameter-sniffing plan problems and silent precision loss. `AddTable` is what passes a whole
truck manifest or a 1000-row telemetry batch in one call.

`Data/Mapping.cs` holds one mapping function per contract type, so the reader-to-DTO projection
exists in exactly one place.

### Configuration (`App.config`)

Four endpoints on the service:

| Address | Binding | Contract |
|---|---|---|
| `net.tcp://localhost:8523/YardTracker/Inventory` | `netTcpBinding` (`StationBinding`) | `IInventoryService` |
| `.../Logistics` | same | `ILogisticsService` |
| `.../Telemetry` | same | `ITelemetryService` |
| `net.tcp://localhost:8524/YardTracker/mex` | `mexTcpBinding` | `IMetadataExchange` |

**Why metadata is on its own port (8524).** Without port sharing, every endpoint on one TCP port
must use identical listener settings, and `mexTcpBinding` cannot match the tuned `StationBinding`.
Putting MEX on its own port is the clean fix. This is the kind of detail that only shows up when
you actually run the thing.

The binding is tuned for handhelds:

| Setting | Value | Why |
|---|---|---|
| `openTimeout` | 5 s | A handheld must find out fast that the network is gone |
| `sendTimeout` | 15 s | Fail fast, queue offline, retry — do not freeze the UI |
| `receiveTimeout` | 10 min | Idle connection lifetime, a different concern |
| `maxReceivedMessageSize` | 4 MB | Big enough for a full item list or a telemetry batch |
| `maxConnections`, `listenBacklog` | 200 | Many devices connecting at shift change |
| `security mode` | `Transport`, Windows credentials, `EncryptAndSign` | Signed and encrypted with no certificate to manage on a domain |

Throttling: 128 concurrent calls, 400 sessions, 528 instances.

`includeExceptionDetailInFaults="false"` — never ship server internals to a device on a plant
network.

For non-domain handhelds on a workgroup, the comments in `App.config` point at
`clientCredentialType="Certificate"`, which must be matched in `station.json` /
`appsettings.json` on the client side.

## `YardTracker.Client` — channel plumbing

Small but load-bearing. It encodes the three things people usually get wrong with WCF clients.

**1. Cache the factory, not the channel.**

```csharp
private readonly ChannelFactory<IInventoryService> _inventory;
```

Creating a `ChannelFactory` is expensive (it builds the binding stack and reflects over the
contract); creating a channel from one is cheap. So: one factory per contract for the process
lifetime, a new channel per call.

**2. Abort a faulted channel, never close or reuse it.**

```csharp
try
{
    var result = await call(channel);
    await Task.Factory.FromAsync(communicationObject.BeginClose, communicationObject.EndClose, null);
    return result;
}
catch
{
    communicationObject.Abort();
    throw;
}
```

Calling `Close()` on a faulted channel throws a *second* exception that masks the first. This is
the single most common WCF client bug. `Dispose()` applies the same rule to the factories.

**3. Tell connectivity failures apart from faults.**

```csharp
public static bool IsConnectivityFailure(Exception exception) => exception switch
{
    FaultException => false,
    CommunicationException or TimeoutException or SocketException => true,
    AggregateException aggregate => aggregate.InnerExceptions.Any(IsConnectivityFailure),
    _ => false
};
```

This one predicate decides system behaviour. A `FaultException` means the service answered and
said no — retrying will not help, so surface it. Anything else means the service was never
reached — so queue the scan offline (station) or buffer the reading (gateway) and retry.
`FaultException` derives from `CommunicationException`, so the order of those switch arms is
load-bearing, and there is a test for it.

`Describe` produces the user-facing string, preferring `fault.Detail.Message` (the operator-readable
text the service chose) over the generic exception message.

Timeouts on the client binding mirror the server: 5 s open, 15 s send, and the security mode is
configurable so a demo can run with `Security = "None"`.

Next: [05-station-app.md](05-station-app.md)
