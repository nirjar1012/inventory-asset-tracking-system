# 13. Glossary

## Yard and steel

| Term | Meaning |
|---|---|
| **Yard** | An outdoor industrial storage site. Here, three of them: North Yard (NYD), South Yard (SYD), and the Coating & Threading Plant (CTP) |
| **Bundle** | Several pipe joints strapped together and handled as one unit. In this system, one `Item` with one tag |
| **Joint** | A single length of pipe, typically 40 ft for line pipe, 31.5 ft for tubing |
| **Heat number** | The identifier of a single melt at the steel mill. Every bundle carries one, and it is the key to a mill test report. If a heat turns out defective, you must be able to find every bundle from it — that is why `HeatNumber` is on `Items` and why `ScanEvents` is never deleted |
| **MTR** (Mill Test Report) | The mill's certificate of chemistry and mechanical properties for a heat |
| **Laydown** | An open ground area for storing material, as opposed to a rack |
| **Rack** | Structured storage, usually for pipe |
| **Dock** | Where trucks load and unload |
| **Bay** | A processing position — the coating line bay, the threading bay |
| **Staging** | A holding area for material about to ship |
| **Quarantine / QA hold** | Where material sits pending inspection or after failing one |
| **Line pipe** | Pipe for pipelines. Graded to API 5L, for example X52, X65 |
| **Casing** | Large-diameter pipe that lines a drilled well. API 5CT, for example J55, P110 |
| **Tubing** | Smaller pipe inside the casing that the fluid actually flows through |
| **Coating** | A protective layer applied in a cure oven |
| **Threading** | Cutting connection threads on pipe ends. Slow, precise, and in this yard the bottleneck |
| **BOL** (Bill of Lading) | The shipping document. Used as a check-out reference |
| **Work order** | The job material is being issued against |
| **Put-away** | Moving received material from the dock to its storage location |
| **FIFO** | First in, first out. Issuing the oldest stock first, which a yard does to control aging |
| **Dwell time** | How long an item sat at one location. High dwell at a process location signals a bottleneck |
| **Throughput** | How much moved in a period, in scans or in tons |

## Devices and capture

| Term | Meaning |
|---|---|
| **Handheld** | A rugged wireless scanner-plus-computer an operator carries. Runs the WPF station |
| **Keyboard wedge** | A scanner that delivers its read as keystrokes, exactly as if typed, usually with a trailing Enter. Requires no driver, which is why it dominates on the floor |
| **RFID** (Radio-Frequency Identification) | Tags read by radio, without line of sight |
| **EPC** (Electronic Product Code) | The identifier on an RFID tag. A 96-bit EPC is 24 hex characters |
| **Fixed reader / portal** | An RFID reader mounted at a gate that reads tags as they pass |
| **LLRP** | Low Level Reader Protocol, the standard for talking to RFID readers. Named as the future replacement for `SimulatedRfidReader` |
| **Kiosk** | A fixed PC running the same station app, typically in the shipping office |
| **Misread** | A scan that produced wrong or unusable text. Logged as an exception, because the misread **rate** is a maintenance signal |
| **Debounce** | Collapsing repeated reads of the same tag within a short window into one logical read. Done at the reader; the idempotency key catches whatever slips through |

## Industrial protocol

| Term | Meaning |
|---|---|
| **PLC** (Programmable Logic Controller) | The industrial computer that runs a machine and exposes its state as registers |
| **Modbus** | A 1979 protocol still ubiquitous on the plant floor. Registers, function codes, big-endian |
| **Modbus TCP** | Modbus carried over TCP/IP, with the MBAP header replacing the serial framing |
| **MBAP** | Modbus Application Protocol header: transaction id, protocol id, length, unit id. Seven bytes |
| **PDU** | Protocol Data Unit: function code plus data. Unchanged from serial Modbus |
| **Unit id** | Which device a request is for. On TCP it addresses a slave behind a gateway |
| **Holding register** | A readable and writable 16-bit value. The `4xxxx` numbers are the conventional one-based PLC numbering; the wire uses zero-based addresses |
| **FC03 / FC06** | Read Holding Registers / Write Single Register — the two function codes used here |
| **Modbus exception response** | An error answer with the function code's high bit set. An **application-level** answer, so the TCP connection stays open |
| **Master / slave** | The polling side and the responding side. The gateway is a master; a PLC (or the simulator) is a slave |
| **Edge gateway** | Software near the equipment that speaks the industrial protocol and forwards to a business system |
| **Store and forward** | Buffering readings with their original timestamps while the upstream system is unreachable. The historian pattern |
| **Historian** | A system that records process values over time |

## Platform and architecture

| Term | Meaning |
|---|---|
| **WPF** | Windows Presentation Foundation, the .NET desktop UI framework used for the station |
| **MVVM** | Model-View-ViewModel, the WPF pattern. Provided here by `CommunityToolkit.Mvvm` |
| **WCF** | Windows Communication Foundation, the .NET service framework used for the middle tier |
| **`netTcpBinding`** | WCF's binary-over-TCP binding. Fast, with transport security |
| **MEX** | Metadata Exchange, the WCF endpoint that serves the WSDL |
| **`ChannelFactory`** | The WCF client object that creates channels. Expensive to build, so cached; channels themselves are cheap and created per call |
| **Fault contract** | The declared error shape an operation can return. Here always `ServiceFault` |
| **Per-call instancing** | A fresh service instance per request, hence no server-side session state |
| **CoreWCF** | The open-source port of WCF to modern .NET. The named migration target for the server |
| **PBIP** | Power BI Project: the report and model stored as text rather than a binary `.pbix` |
| **TMDL** | Tabular Model Definition Language, the text format for a Power BI semantic model |
| **PBIR** | The JSON format for a Power BI report definition |
| **DAX** | The formula language for Power BI measures |
| **RDL** | Report Definition Language, the XML format of an SSRS report |
| **SSIS / SSRS** | SQL Server Integration Services (ETL) and Reporting Services |
| **`dtexec`** | The SSIS package execution utility |
| **PnP PowerShell** | The community module for automating SharePoint |
| **LocalDB** | A lightweight, per-user SQL Server instance for development. Not reachable by a service account, which is why SSRS needs a full instance |

## Data and correctness concepts

| Term | Meaning |
|---|---|
| **Idempotent** | Doing it twice has the same effect as doing it once. Here, achieved with `ClientScanId` |
| **`ClientScanId`** | A GUID generated **on the device** when a tag is read. The idempotency key. Retries reuse it |
| **At-least-once delivery** | The sender retries until acknowledged, so a message may arrive more than once. Combined with an idempotent receiver, this gives exactly-once *behaviour* |
| **`rowversion`** | A SQL Server column assigned a database-wide monotonically increasing value at commit. Follows **arrival** order, not clock order, which is why it is the ETL watermark |
| **`MIN_ACTIVE_ROWVERSION()`** | The lowest rowversion any open transaction could still use. Everything below it is committed and final |
| **Watermark** | How far an incremental load has got |
| **`UPDLOCK, HOLDLOCK`** | Table hints that take an update lock at read time and hold it to the end of the transaction. Makes read-then-write safe without deadlocking on a lock upgrade |
| **`READ_COMMITTED_SNAPSHOT`** | A database option where readers see a consistent snapshot instead of blocking on writers |
| **Business day** | The calendar day in the **site's** local time, converted with `AT TIME ZONE` from stored UTC. A 9 pm Houston scan belongs to that day, not the next UTC day |
| **Star schema** | Fact tables joined to dimension tables. The shape BI tools are built for |
| **Grain** | What one row of a fact table represents. Here: business date × location × product category |
| **Conformed dimension** | A dimension shared by several facts, which is what lets one slicer filter them all |
| **TVP** (Table-Valued Parameter) | A table passed as a single stored-procedure parameter. Used for truck manifests and telemetry batches |
| **Correlation id** | A short reference returned to the client with a fault and written to the service log, so support can find the details without the client ever seeing a stack trace |
| **Clock skew** | A device clock disagreeing with the server. Rejected only when the device is **ahead** by more than five minutes; behind is legitimate and means an offline scan |

## Codes used in this system

**Item statuses:** `InYard`, `CheckedOut`, `InTransit`

**Scan event types:** `Receive`, `CheckIn`, `CheckOut`, `Move`, `LoadTruck`, `Deliver`

**Scan outcomes:** `Accepted`, `Duplicate`, `Rejected`

**Exception types:** `UnknownTag`, `UnknownLocation`, `UnknownOperator`, `UnknownStation`,
`UnknownProduct`, `InvalidState`, `DuplicateTag`, `ClockSkew`, `WrongSite`

**Fault codes:** `Validation`, `Rejected`, `Unavailable`, `Internal`

**Machine states:** 0 Offline, 1 Idle, 2 Running, 3 Fault, 4 Maintenance

**Location types:** `Rack`, `Laydown`, `Bay`, `Dock`, `Staging`, `Quarantine`

**Product categories:** `Pipe`, `Casing`, `Tubing`, `Plate`, `Beam`, `Bar`

**Operator roles:** `Operator`, `Supervisor`, `Driver`

**Sites:** `NYD` North Yard · `SYD` South Yard · `CTP` Coating & Threading Plant

**Machines:** `CRN-N1` overhead crane (unit 1) · `SAW-C1` band saw (unit 2) ·
`OVN-C1` cure oven (unit 3) · `THR-C1` CNC threader (unit 4)

Back to the [index](README.md).
