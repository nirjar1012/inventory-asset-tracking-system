using System.Collections.ObjectModel;
using System.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YardTracker.Client;
using YardTracker.Contracts;
using YardTracker.Station.Services;

namespace YardTracker.Station.ViewModels;

public enum ScanMode
{
    CheckIn,
    CheckOut,
    Move,
    Receive,
    Lookup
}

public enum BannerKind
{
    Info,
    Success,
    Duplicate,
    Queued,
    Error
}

public sealed record ResultBanner(BannerKind Kind, string Title, string Detail);

public partial class SessionScan : ObservableObject
{
    public required Guid ClientScanId { get; init; }
    public required DateTime Time { get; init; }
    public required string Action { get; init; }
    public required string Tag { get; init; }

    [ObservableProperty] private string _outcome = "Sending";
    [ObservableProperty] private string _message = string.Empty;
}

public partial class ScanViewModel : ObservableObject
{
    private readonly YardTrackerClient _client;
    private readonly ScanSubmitter _submitter;
    private readonly SimulatedRfidReader _rfid;
    private StationSession? _session;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsLocation), nameof(IsReceiveMode), nameof(NeedsReference), nameof(ModeHint), nameof(ReferenceLabel))]
    private ScanMode _mode = ScanMode.CheckIn;

    [ObservableProperty] private string _scanText = string.Empty;
    [ObservableProperty] private LocationInfo? _selectedLocation;
    [ObservableProperty] private ProductInfo? _selectedProduct;
    [ObservableProperty] private string _heatNumber = string.Empty;
    [ObservableProperty] private string _piecesText = "10";
    [ObservableProperty] private string _reference = string.Empty;
    [ObservableProperty] private ResultBanner? _banner;
    [ObservableProperty] private ItemInfo? _currentItem;
    [ObservableProperty] private bool _isBusy;

    public ScanViewModel(YardTrackerClient client, ScanSubmitter submitter, SimulatedRfidReader rfid)
    {
        _client = client;
        _submitter = submitter;
        _rfid = rfid;
        _submitter.Synced += OnSynced;
    }

    /// <summary>Raised when the view should put keyboard focus back in the scan box (keyboard-wedge scanners type there).</summary>
    public event Action? FocusScanRequested;

    public ObservableCollection<LocationInfo> Locations { get; } = [];
    public ObservableCollection<ProductInfo> Products { get; } = [];
    public ObservableCollection<ScanEventInfo> History { get; } = [];
    public ObservableCollection<SessionScan> SessionLog { get; } = [];

    public bool NeedsLocation => Mode is ScanMode.CheckIn or ScanMode.Move or ScanMode.Receive;
    public bool IsReceiveMode => Mode == ScanMode.Receive;
    public bool NeedsReference => Mode is ScanMode.CheckIn or ScanMode.CheckOut or ScanMode.Receive;

    public string ReferenceLabel => Mode switch
    {
        ScanMode.CheckOut => "WORK ORDER / BOL",
        ScanMode.Receive => "PURCHASE ORDER",
        _ => "REFERENCE"
    };

    public string ModeHint => Mode switch
    {
        ScanMode.CheckIn => "Scan material returning to the yard. It is placed at the target location.",
        ScanMode.CheckOut => "Scan material leaving the yard for a job or customer.",
        ScanMode.Move => "Scan an item to relocate it within this site.",
        ScanMode.Receive => "Scan a new tag to register incoming material from a mill or supplier.",
        _ => "Scan any tag to see where it is and where it has been."
    };

    partial void OnModeChanged(ScanMode value) => FocusScanRequested?.Invoke();

    public async Task StartAsync(StationSession session)
    {
        var locations = await _client.InventoryAsync(s => s.GetLocationsAsync(null));
        var products = await _client.InventoryAsync(s => s.GetProductsAsync());

        _session = session;
        Locations.ReplaceWith(locations);
        Products.ReplaceWith(products);
        SelectedLocation = Locations.FirstOrDefault(l => l.LocationCode == session.DefaultLocationCode) ?? Locations.FirstOrDefault();
        SelectedProduct = Products.FirstOrDefault();
        SessionLog.Clear();
        History.Clear();
        CurrentItem = null;
        Mode = ScanMode.CheckIn;
        Banner = new ResultBanner(BannerKind.Info, $"Signed in as {session.OperatorName}", $"{session.StationName}. Ready to scan.");
    }

    public void Stop() => _session = null;

    [RelayCommand]
    private async Task SubmitScanAsync()
    {
        if (_session == null || IsBusy)
            return;

        var raw = ScanText;
        ScanText = string.Empty;
        var code = ScanCodes.Parse(raw);

        try
        {
            IsBusy = true;
            switch (code.Kind)
            {
                case ScanCodeKind.Empty:
                    return;

                case ScanCodeKind.Location:
                    var location = Locations.FirstOrDefault(l => l.LocationCode == code.Value);
                    if (location == null)
                    {
                        Banner = new ResultBanner(BannerKind.Error, "Unknown location label", code.Value);
                        Feedback(false);
                    }
                    else
                    {
                        SelectedLocation = location;
                        Banner = new ResultBanner(BannerKind.Info, $"Target location: {location.LocationCode}", location.Name);
                    }
                    return;

                case ScanCodeKind.Badge:
                    Banner = new ResultBanner(BannerKind.Info, "Badge scanned", "Sign out first to change operator.");
                    return;

                case ScanCodeKind.Invalid when Mode is ScanMode.Lookup or ScanMode.Receive:
                    Banner = new ResultBanner(BannerKind.Error, "Unreadable tag", $"'{raw.Trim()}' is not a valid tag. Rescan.");
                    Feedback(false);
                    return;
            }

            // Malformed tags in check-in/out/move modes still go to the server so the misread is logged.
            if (Mode == ScanMode.Lookup)
                await LookupAsync(code.Value);
            else
                await RecordAsync(code.Value);
        }
        catch (Exception ex)
        {
            var unreachable = YardTrackerClient.IsConnectivityFailure(ex);
            Banner = new ResultBanner(BannerKind.Error, unreachable ? "Service unreachable" : "Scan failed", YardTrackerClient.Describe(ex));
            Feedback(false);
        }
        finally
        {
            IsBusy = false;
            FocusScanRequested?.Invoke();
        }
    }

    [RelayCommand]
    private async Task SimulateRfidReadAsync()
    {
        if (_session == null)
            return;

        try
        {
            var tag = await _rfid.ReadAsync(Mode, SelectedLocation?.SiteCode);
            if (tag == null)
            {
                Banner = new ResultBanner(BannerKind.Info, "No tag in range", "The simulated reader found no suitable items for this mode.");
                return;
            }
            ScanText = tag;
        }
        catch (Exception ex)
        {
            Banner = new ResultBanner(BannerKind.Error, "RFID reader", YardTrackerClient.Describe(ex));
            return;
        }

        await SubmitScanAsync();
    }

    private async Task RecordAsync(string tag)
    {
        var session = _session!;
        QueuedScan scan;

        if (Mode == ScanMode.Receive)
        {
            if (SelectedProduct == null || SelectedLocation == null || string.IsNullOrWhiteSpace(HeatNumber)
                || !int.TryParse(PiecesText, out var pieces) || pieces <= 0)
            {
                Banner = new ResultBanner(BannerKind.Error, "Receiving details missing", "Choose a product and location, and enter the heat number and piece count.");
                Feedback(false);
                return;
            }

            scan = new QueuedScan
            {
                Kind = QueuedScanKind.Receive,
                Receive = new ReceiveRequest
                {
                    ClientScanId = Guid.NewGuid(),
                    TagId = tag,
                    ProductCode = SelectedProduct.ProductCode,
                    HeatNumber = HeatNumber.Trim(),
                    Pieces = pieces,
                    LocationCode = SelectedLocation.LocationCode,
                    OperatorBadge = session.BadgeNumber,
                    StationCode = session.StationCode,
                    ScannedAtUtc = DateTime.UtcNow,
                    Reference = NullIfBlank(Reference)
                }
            };
        }
        else
        {
            if (NeedsLocation && SelectedLocation == null)
            {
                Banner = new ResultBanner(BannerKind.Error, "Pick a target location", "Choose a location or scan a LOC: label first.");
                Feedback(false);
                return;
            }

            scan = new QueuedScan
            {
                Kind = Mode switch
                {
                    ScanMode.CheckIn => QueuedScanKind.CheckIn,
                    ScanMode.CheckOut => QueuedScanKind.CheckOut,
                    _ => QueuedScanKind.Move
                },
                Scan = new ScanRequest
                {
                    ClientScanId = Guid.NewGuid(),
                    TagId = tag,
                    LocationCode = NeedsLocation ? SelectedLocation!.LocationCode : null,
                    OperatorBadge = session.BadgeNumber,
                    StationCode = session.StationCode,
                    ScannedAtUtc = DateTime.UtcNow,
                    Reference = Mode == ScanMode.Move ? null : NullIfBlank(Reference)
                }
            };
        }

        var entry = new SessionScan { ClientScanId = scan.ClientScanId, Time = DateTime.Now, Action = ActionName(scan.Kind), Tag = tag };
        SessionLog.Insert(0, entry);

        var submitted = await _submitter.SubmitAsync(scan);
        if (submitted.Queued)
        {
            entry.Outcome = "Queued";
            entry.Message = "Saved offline; syncs automatically.";
            Banner = new ResultBanner(BannerKind.Queued, "Saved offline", $"{entry.Action} of {tag} is queued on this device and will sync when the network is back.");
            Feedback(true);
            return;
        }

        var result = submitted.Result!;
        entry.Outcome = result.Outcome.ToString();
        entry.Message = result.Message;
        Banner = result.Outcome switch
        {
            ScanOutcome.Accepted => new ResultBanner(BannerKind.Success, $"{entry.Action}: OK", result.Message),
            ScanOutcome.Duplicate => new ResultBanner(BannerKind.Duplicate, "Already recorded", result.Message),
            _ => new ResultBanner(BannerKind.Error, $"Rejected: {Humanize(result.ExceptionType)}", result.Message)
        };
        Feedback(result.Outcome != ScanOutcome.Rejected);

        CurrentItem = result.Item;
        await LoadHistoryAsync(result.Item?.TagId);
    }

    private async Task LookupAsync(string tag)
    {
        var item = await _client.InventoryAsync(s => s.LookupItemAsync(tag));
        CurrentItem = item;

        if (item == null)
        {
            Banner = new ResultBanner(BannerKind.Error, "Tag not found", $"{tag} is not registered.");
            Feedback(false);
            History.Clear();
            return;
        }

        var where = item.LocationCode != null ? $"at {item.LocationCode} ({item.LocationName})" : item.Status == "InTransit" ? "on a truck" : "out of the yard";
        Banner = new ResultBanner(BannerKind.Info, $"{item.TagId}: {item.Status}", $"{item.Description} {where}. Last moved {item.DaysSinceLastMove} days ago.");
        Feedback(true);
        await LoadHistoryAsync(item.TagId);
    }

    private async Task LoadHistoryAsync(string? tag)
    {
        if (tag == null)
        {
            History.Clear();
            return;
        }

        try
        {
            History.ReplaceWith(await _client.InventoryAsync(s => s.GetItemHistoryAsync(tag)));
        }
        catch (Exception ex) when (YardTrackerClient.IsConnectivityFailure(ex))
        {
            History.Clear();
        }
    }

    private void OnSynced(QueuedScan scan, ScanResult result)
    {
        var entry = SessionLog.FirstOrDefault(e => e.ClientScanId == scan.ClientScanId);
        if (entry != null)
        {
            entry.Outcome = result.Outcome.ToString();
            entry.Message = "Synced: " + result.Message;
        }
    }

    private static string ActionName(QueuedScanKind kind) => kind switch
    {
        QueuedScanKind.CheckIn => "Check in",
        QueuedScanKind.CheckOut => "Check out",
        QueuedScanKind.Move => "Move",
        _ => "Receive"
    };

    private static string Humanize(string? exceptionType) => exceptionType switch
    {
        "UnknownTag" => "unknown tag",
        "InvalidState" => "not allowed",
        "WrongSite" => "wrong site",
        "UnknownLocation" => "unknown location",
        "DuplicateTag" => "tag already in use",
        null => "not allowed",
        _ => exceptionType
    };

    private static string? NullIfBlank(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Audible confirmation: operators look at the steel, not the screen.</summary>
    private static void Feedback(bool ok)
    {
        if (ok)
            SystemSounds.Asterisk.Play();
        else
            SystemSounds.Hand.Play();
    }
}
