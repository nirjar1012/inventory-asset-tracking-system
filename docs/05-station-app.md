# 5. The scanning station (WPF)

`src/YardTracker.Station` is the application yard operators actually touch. It runs on a
wireless handheld, a vehicle-mount terminal or a kiosk PC. Everything about it is shaped by
two facts: **the user is wearing gloves in daylight**, and **the network is not reliable**.

Target framework `net10.0-windows` with `UseWPF`, MVVM via `CommunityToolkit.Mvvm`.

## Startup and configuration

`App.xaml.cs` does four things: install a dispatcher exception handler (an unhandled UI
exception shows a message box instead of killing the app mid-shift), load settings, create the
`YardTrackerClient` and the offline queue, and show the shell.

Settings come from `station.json` beside the executable, overridable per instance on the command
line:

```powershell
dotnet run --project src\YardTracker.Station -- --station HH-02 --service net.tcp://plant-srv:8523/YardTracker/
```

| Setting | Default | Purpose |
|---|---|---|
| `StationCode` | `HH-01` | Identifies the device. Every scan is attributed to it, and it names the offline queue file |
| `Service.BaseAddress` | `net.tcp://localhost:8523/YardTracker/` | Where the service is |
| `Service.Security` | `Transport` | Must match the server binding |
| `Service.TimeoutSeconds` | `10` | Fail fast so a scan queues instead of hanging |
| `OfflineRetrySeconds` | `10` | How often to try to drain the queue |

One executable, many devices: the same build is deployed everywhere and only `station.json`
differs. The offline queue file name includes the station code, so two stations on one PC during
a demo do not collide.

## Shell and layout

`ShellViewModel` owns the session and the four tab view-models, plus two `DispatcherTimer`s: a
one-second clock for the header, and an `OfflineRetrySeconds` sync timer.

The window is a sign-in screen until a badge is accepted, then a header plus four tabs:

| Tab | View model | Shows |
|---|---|---|
| **Scan** | `ScanViewModel` | The working screen: mode tiles, scan box, result banner, item card, session log |
| **Inventory** | `InventoryViewModel` | Locations with utilization, item lists, open exceptions, filtered by site |
| **Fleet map** | `FleetViewModel` | Live truck positions and GPS trails on a projected site map |
| **Machines** | `MachinesViewModel` | Modbus telemetry tiles: temperature, state, cycles, fault |

The header always shows operator, station, clock, and the connectivity state —
**Online** or **Offline, _n_ pending**. That indicator is the single most important piece of UI
in the app: an operator must be able to tell at a glance whether their scans are reaching the
server, and to know that queued scans are safe.

## Sign-in

An operator scans their `BADGE:1001` label or types `1001`; `ScanCodes.Parse` accepts both.
`SignInAsync` returns a `StationSession` with name, role, station name, device type, default
location and server time. The default location pre-selects the right target, so an operator at
the receiving dock does not pick a location on every scan.

Three demo badges are printed on the sign-in screen: 1001 operator, 2001 supervisor, 3001 driver.

## The Scan tab

### Five modes

| Mode | What it does | Needs a location | Reference field |
|---|---|---|---|
| **Check in** | Material returning to the yard, placed at the target location | yes | return reference |
| **Check out** | Material leaving for a job or customer | no | work order / BOL |
| **Move** | Relocate within the same site | yes | none |
| **Receive** | Register a brand-new tag from a mill or supplier | yes | purchase order |
| **Lookup** | Read-only: show the item card and history | no | none |

Mode is chosen with large tiles. Changing mode re-focuses the scan box and re-labels the
reference field, so the screen always says what *this* mode needs.

### Keyboard-wedge scanner handling

A keyboard-wedge scanner types into whatever control has focus, then sends Enter. If the operator
last tapped a mode tile or a dropdown, those keystrokes would be lost — and a lost read on a
shop floor means an operator scanning the same bundle three times.

`ScanView` solves it by overriding two preview events at the view level:

```csharp
protected override void OnPreviewTextInput(TextCompositionEventArgs e)
{
    if (e.OriginalSource is not TextBox && e.Text.Length > 0 && !char.IsControl(e.Text[0]))
    {
        ScanBox.Text += e.Text;
        Keyboard.Focus(ScanBox);
        ScanBox.CaretIndex = ScanBox.Text.Length;
        e.Handled = true;
    }
    base.OnPreviewTextInput(e);
}
```

Any printable character typed anywhere that is not already a text box is rerouted into the scan
box and focus follows it. `OnPreviewKeyDown` does the same for the trailing Enter. The scan box
is also re-focused on load, on becoming visible, on mode change and after every submission — so
the device is always ready for the next read.

### Simulate RFID read

`SimulatedRfidReader` stands in for reader hardware so the whole flow can be demonstrated without
a portal. It picks a tag that suits the current mode: a fresh 96-bit EPC for Receive, a
`CheckedOut` item for Check in, an `InYard` item at the current site otherwise. A real LLRP
reader would raise the same tag string into the same code path.

### What happens on a scan

1. `ScanCodes.Parse` classifies the raw text.
2. A `LOC:` label switches the target location and shows an info banner — no server call.
   A `BADGE:` label is recognised and refused politely ("Sign out first to change operator").
3. An unreadable tag is rejected locally **only in Lookup and Receive**. In check-in, check-out
   and move modes a malformed tag is still sent to the server, deliberately, **so the misread is
   logged as a `ScanException`**. Misread rate per station is a real maintenance signal — a
   scanner with a dirty window or a batch of damaged labels shows up in the data.
4. Otherwise a `ScanRequest` (or `ReceiveRequest`) is built with a **new `ClientScanId`** and the
   device clock, and handed to `ScanSubmitter`.
5. The result becomes a colour-coded banner: green Accepted, amber Duplicate ("Already
   recorded"), red Rejected with a humanised reason, blue Queued ("Saved offline"). An entry is
   added to the session log either way.

Receive mode validates its own extra fields locally first — product, location, heat number and a
positive piece count — because sending an obviously incomplete receipt to the server just to be
told no would waste a round trip on a slow link.

## The offline queue

This is the heart of the application's reliability story. `Services/StationServices.cs`.

### `OfflineScanQueue`

Backed by `%LOCALAPPDATA%\YardTracker\offline-queue-<station>.json`. Every mutation saves
immediately, and the save is **atomic**:

```csharp
private void Save()
{
    var temp = _path + ".tmp";
    File.WriteAllText(temp, JsonSerializer.Serialize(_items, JsonOptions));
    File.Move(temp, _path, overwrite: true);
}
```

Write to a temporary file, then move over the original. A battery that dies mid-write leaves
either the old complete file or the new complete file — never a truncated one. On a device that
runs until it dies, that matters.

The queue is loaded in the constructor, so scans survive not just a network outage but an app
restart or a dead battery.

### `ScanSubmitter`

```csharp
public async Task<SubmitResult> SubmitAsync(QueuedScan scan)
{
    // Keep scans in order: while older scans are still queued, new ones wait behind them.
    if (queue.Count > 0)
    {
        queue.Enqueue(scan);
        _ = FlushAsync();
        return new SubmitResult(null);
    }

    try
    {
        var result = await SendAsync(scan);
        SetOnline(true);
        return new SubmitResult(result);
    }
    catch (Exception ex) when (YardTrackerClient.IsConnectivityFailure(ex))
    {
        queue.Enqueue(scan);
        SetOnline(false);
        return new SubmitResult(null);
    }
}
```

Three behaviours are encoded here, and each one matters:

**Ordering.** If anything is queued, a new scan goes behind it rather than jumping the line.
Without this, a `Move` could be applied before the `Receive` that created the item, and the move
would be rejected as `UnknownTag` — a phantom error caused purely by transmission order.

**Only connectivity failures queue.** The exception filter is
`YardTrackerClient.IsConnectivityFailure`. A `FaultException` means the service answered and said
no; retrying will not help, so it surfaces immediately.

**Flush semantics.** `FlushAsync` is guarded by `_flushGate.WaitAsync(0)` so overlapping timer
ticks cannot double-send. It walks the queue in order; a connectivity failure stops the flush and
leaves the rest queued; a *fault* during replay marks that one scan rejected and removes it,
because retrying it forever would block every scan behind it.

`ProbeAsync` is a cheap `GetSites` round trip used to notice the network is back when nothing is
queued, so the header flips to Online promptly.

Every replayed scan keeps its original `ClientScanId` and `ScannedAtUtc`. The server's idempotency
check makes the replay safe, and the preserved device timestamp means history records when the
steel actually moved — not when the Wi-Fi came back. The ETL is built to handle exactly that
(see [08-etl-pipeline.md](08-etl-pipeline.md)).

**To see it work:** with the station signed in, stop the service and keep scanning. The header
shows "Offline, _n_ pending". Start the service; the queue drains in order with the original
timestamps.

## The other three tabs

**Inventory** — locations with item count, pieces, tons and utilization percentage, plus item
lists and open exceptions, filtered by site (`All sites`, `NYD`, `SYD`, `CTP`). This is the
supervisor's live view without leaving the device.

**Fleet map** — `MapProjection` converts latitude and longitude to pixels with a cosine-of-latitude
correction so the map is not stretched, then draws sites as markers and GPS breadcrumbs as
polylines. Truck cards alongside show route, driver, item count and last ping age. Polls
`GetFleetStatusAsync` on a timer.

**Machines** — one `MachineTile` per machine: temperature with a bar scaled against its own
warn and alarm limits, state, cycle count, fault code and reading age. `Level` drives the colour
(normal, warning, alarm). Limits are per machine, so the oven at 232 °C reads normal while the
crane at 90 °C reads warning.

## Presentation

`Theme.xaml` defines a dark, high-contrast palette with large touch targets — readable in
daylight, under sodium lamps, and with gloves on. `Converters.cs` supplies the small conversions
XAML needs: bool and null to visibility, UTC to local time (so operators see their own clock while
storage stays UTC), enum equality for the mode tiles, and key-to-brush for banner and status
colours.

Next: [06-modbus-and-gateway.md](06-modbus-and-gateway.md)
