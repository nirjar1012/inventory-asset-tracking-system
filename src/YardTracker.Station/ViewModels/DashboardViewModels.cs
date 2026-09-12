using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YardTracker.Client;
using YardTracker.Contracts;

namespace YardTracker.Station.ViewModels;

// ====================================================================== inventory

public partial class InventoryViewModel(YardTrackerClient client) : ObservableObject
{
    public ObservableCollection<LocationInventory> Locations { get; } = [];
    public ObservableCollection<ItemInfo> Items { get; } = [];
    public ObservableCollection<ScanExceptionInfo> Exceptions { get; } = [];
    public string[] SiteFilters { get; } = ["All sites", "NYD", "SYD", "CTP"];

    [ObservableProperty] private string _siteFilter = "All sites";
    [ObservableProperty] private LocationInventory? _selectedLocation;
    [ObservableProperty] private string _summary = string.Empty;
    [ObservableProperty] private string? _error;

    partial void OnSiteFilterChanged(string value) => _ = RefreshAsync();

    partial void OnSelectedLocationChanged(LocationInventory? value) => _ = LoadItemsAsync(value);

    [RelayCommand]
    public async Task RefreshAsync()
    {
        try
        {
            var site = SiteFilter == SiteFilters[0] ? null : SiteFilter;
            var selected = SelectedLocation?.LocationCode;

            var rows = await client.InventoryAsync(s => s.GetInventoryByLocationAsync(site));
            var exceptions = await client.InventoryAsync(s => s.GetOpenExceptionsAsync(100));

            Locations.ReplaceWith(rows);
            Exceptions.ReplaceWith(exceptions);
            Summary = $"{rows.Sum(r => r.ItemCount):N0} items  ·  {rows.Sum(r => r.Pieces):N0} pieces  ·  {rows.Sum(r => r.WeightTons):N0} tons  ·  {exceptions.Length} open scan exceptions";
            SelectedLocation = Locations.FirstOrDefault(l => l.LocationCode == selected) ?? Locations.FirstOrDefault(l => l.ItemCount > 0);
            Error = null;
        }
        catch (Exception ex)
        {
            Error = YardTrackerClient.Describe(ex);
        }
    }

    private async Task LoadItemsAsync(LocationInventory? location)
    {
        if (location == null)
        {
            Items.Clear();
            return;
        }

        try
        {
            var items = await client.InventoryAsync(s => s.SearchItemsAsync(new ItemSearch { LocationCode = location.LocationCode, Status = "InYard", Top = 500 }));
            if (SelectedLocation == location)
                Items.ReplaceWith(items.OrderByDescending(i => i.DaysSinceLastMove));
        }
        catch (Exception ex)
        {
            Error = YardTrackerClient.Describe(ex);
        }
    }
}

// ====================================================================== fleet map

public sealed record MapSite(string Code, string Name, double X, double Y);

public sealed record MapTruck(string TruckCode, double X, double Y, int Heading, bool OnDelivery);

public sealed record TruckCard(string TruckCode, string Status, string Route, string Detail, string LastPing);

/// <summary>Equirectangular projection of the site area onto a fixed-size canvas. Accurate enough over ~30 miles.</summary>
public sealed class MapProjection
{
    private const double MilesPerDegreeLatitude = 69.0;
    private readonly double _minLon, _maxLat, _cosLat, _scale, _offsetX, _offsetY;

    public MapProjection(IReadOnlyCollection<SiteInfo> sites, double width, double height, double padding)
    {
        var minLat = sites.Min(s => s.Latitude);
        var maxLat = sites.Max(s => s.Latitude);
        _minLon = sites.Min(s => s.Longitude);
        var maxLon = sites.Max(s => s.Longitude);
        _maxLat = maxLat;
        _cosLat = Math.Cos((minLat + maxLat) / 2 * Math.PI / 180);

        var spanX = Math.Max(0.01, (maxLon - _minLon) * _cosLat);
        var spanY = Math.Max(0.01, maxLat - minLat);
        _scale = Math.Min((width - 2 * padding) / spanX, (height - 2 * padding) / spanY);
        _offsetX = (width - spanX * _scale) / 2;
        _offsetY = (height - spanY * _scale) / 2;
    }

    public double PixelsPerMile => _scale / MilesPerDegreeLatitude;

    public double X(double longitude) => _offsetX + (longitude - _minLon) * _cosLat * _scale;

    public double Y(double latitude) => _offsetY + (_maxLat - latitude) * _scale;
}

public partial class FleetViewModel : ObservableObject
{
    public const double MapWidth = 900;
    public const double MapHeight = 560;

    private readonly YardTrackerClient _client;
    private readonly DispatcherTimer _timer;
    private MapProjection? _projection;
    private bool _refreshing;

    [ObservableProperty] private string? _error;
    [ObservableProperty] private double _scaleBarWidth;

    public FleetViewModel(YardTrackerClient client)
    {
        _client = client;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick += async (_, _) => await RefreshAsync();
    }

    public ObservableCollection<MapSite> Sites { get; } = [];
    public ObservableCollection<MapTruck> Trucks { get; } = [];
    public ObservableCollection<PointCollection> Routes { get; } = [];
    public ObservableCollection<TruckCard> TruckCards { get; } = [];

    public void Start()
    {
        _timer.Start();
        _ = RefreshAsync();
    }

    public void Stop() => _timer.Stop();

    private async Task RefreshAsync()
    {
        if (_refreshing)
            return;
        _refreshing = true;

        try
        {
            if (_projection == null)
            {
                var sites = await _client.LogisticsAsync(s => s.GetSitesAsync());
                _projection = new MapProjection(sites, MapWidth, MapHeight, padding: 90);
                Sites.ReplaceWith(sites.Select(s => new MapSite(s.SiteCode, s.Name, _projection.X(s.Longitude), _projection.Y(s.Latitude))));
                ScaleBarWidth = _projection.PixelsPerMile * 5;
            }

            var fleet = await _client.LogisticsAsync(s => s.GetFleetStatusAsync());

            var routes = new List<PointCollection>();
            foreach (var truck in fleet.Where(t => t.DeliveryId != null))
            {
                var deliveryId = truck.DeliveryId!.Value;
                var route = await _client.LogisticsAsync(s => s.GetDeliveryRouteAsync(deliveryId));
                routes.Add(new PointCollection(route.Select(p => new Point(_projection.X(p.Longitude), _projection.Y(p.Latitude)))));
            }

            Routes.ReplaceWith(routes);
            Trucks.ReplaceWith(fleet
                .Where(t => t.Latitude != null && t.Longitude != null)
                .Select(t => new MapTruck(t.TruckCode, _projection.X(t.Longitude!.Value), _projection.Y(t.Latitude!.Value), t.HeadingDeg ?? 0, t.DeliveryId != null)));
            TruckCards.ReplaceWith(fleet.Select(ToCard));
            Error = null;
        }
        catch (Exception ex)
        {
            Error = YardTrackerClient.Describe(ex);
        }
        finally
        {
            _refreshing = false;
        }
    }

    private static TruckCard ToCard(TruckStatus t)
    {
        var lastPing = t.LastPingAtUtc is DateTime ping ? "Last GPS ping " + MachineTile.Age(DateTime.UtcNow - ping) : "No GPS data";
        return t.DeliveryId == null
            ? new TruckCard(t.TruckCode, "Idle", t.Description, "Parked, no active delivery", lastPing)
            : new TruckCard(t.TruckCode, "OnDelivery", $"#{t.DeliveryId}  {t.FromSiteCode} → {t.ToSiteCode}",
                $"{t.DriverName}  ·  {t.ItemCount} items  ·  {t.SpeedMph ?? 0:0} mph", lastPing);
    }
}

// ====================================================================== machines

public sealed class MachineTile(MachineStatus status)
{
    public MachineStatus Status { get; } = status;

    public string TemperatureText => Status.TemperatureC is decimal t ? $"{t:0.0} °C" : "--";

    public string Level =>
        Status.TemperatureC is not decimal t ? "Normal"
        : Status.TempAlarmC is decimal alarm && t >= alarm ? "Alarm"
        : Status.TempWarnC is decimal warn && t >= warn ? "Warning"
        : "Normal";

    public double TemperaturePercent =>
        Status.TemperatureC is decimal t && Status.TempAlarmC is decimal alarm && alarm > 0
            ? Math.Clamp((double)(t / alarm) * 100, 0, 100)
            : 0;

    public string Limits => $"Warn {Status.TempWarnC:0} °C  ·  Alarm {Status.TempAlarmC:0} °C";

    public string Cycles => Status.CycleCount?.ToString("N0") ?? "--";

    public string Fault => Status.FaultCode is > 0 ? $"Code {Status.FaultCode}" : "None";

    public string Updated => Status.LastReadingAtUtc is DateTime at ? "Updated " + Age(DateTime.UtcNow - at) : "No readings yet";

    public string Subtitle => $"{Status.MachineCode}  ·  {Status.MachineType}  ·  {Status.LocationCode}";

    public static string Age(TimeSpan age) => age.TotalSeconds switch
    {
        < 90 => $"{Math.Max(0, age.TotalSeconds):0}s ago",
        < 5400 => $"{age.TotalMinutes:0} min ago",
        < 172800 => $"{age.TotalHours:0} h ago",
        _ => $"{age.TotalDays:0} days ago"
    };
}

public partial class MachinesViewModel : ObservableObject
{
    private readonly YardTrackerClient _client;
    private readonly DispatcherTimer _timer;

    [ObservableProperty] private string? _error;

    public MachinesViewModel(YardTrackerClient client)
    {
        _client = client;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _timer.Tick += async (_, _) => await RefreshAsync();
    }

    public ObservableCollection<MachineTile> Machines { get; } = [];

    public void Start()
    {
        _timer.Start();
        _ = RefreshAsync();
    }

    public void Stop() => _timer.Stop();

    private async Task RefreshAsync()
    {
        try
        {
            var statuses = await _client.TelemetryAsync(s => s.GetMachineStatusAsync());
            Machines.ReplaceWith(statuses.Select(s => new MachineTile(s)));
            Error = null;
        }
        catch (Exception ex)
        {
            Error = YardTrackerClient.Describe(ex);
        }
    }
}
