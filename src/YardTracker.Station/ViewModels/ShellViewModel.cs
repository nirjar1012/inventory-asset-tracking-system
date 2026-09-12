using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YardTracker.Client;
using YardTracker.Contracts;
using YardTracker.Station.Services;

namespace YardTracker.Station.ViewModels;

public partial class ShellViewModel : ObservableObject
{
    private readonly YardTrackerClient _client;
    private readonly ScanSubmitter _submitter;
    private readonly OfflineScanQueue _queue;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSignedIn))]
    private StationSession? _session;

    [ObservableProperty] private string _badgeInput = string.Empty;
    [ObservableProperty] private string? _signInError;
    [ObservableProperty] private bool _isOnline = true;
    [ObservableProperty] private int _pendingCount;
    [ObservableProperty] private string _clock = DateTime.Now.ToString("ddd HH:mm:ss");
    [ObservableProperty] private int _selectedTab;

    public ShellViewModel(StationSettings settings, YardTrackerClient client, OfflineScanQueue queue)
    {
        Settings = settings;
        _client = client;
        _queue = queue;
        _submitter = new ScanSubmitter(client, queue);
        _submitter.ConnectivityChanged += online => IsOnline = online;
        queue.Changed += () => PendingCount = queue.Count;
        PendingCount = queue.Count;

        Scan = new ScanViewModel(client, _submitter, new SimulatedRfidReader(client));
        Inventory = new InventoryViewModel(client);
        Fleet = new FleetViewModel(client);
        Machines = new MachinesViewModel(client);

        var clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        clock.Tick += (_, _) => Clock = DateTime.Now.ToString("ddd HH:mm:ss");
        clock.Start();

        var sync = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Max(3, settings.OfflineRetrySeconds)) };
        sync.Tick += async (_, _) => await SyncAsync();
        sync.Start();
    }

    public StationSettings Settings { get; }
    public ScanViewModel Scan { get; }
    public InventoryViewModel Inventory { get; }
    public FleetViewModel Fleet { get; }
    public MachinesViewModel Machines { get; }

    public bool IsSignedIn => Session != null;

    public string DemoBadges => "Demo badges:  1001 Maria Alvarez (operator)  ·  2001 Dana Brooks (supervisor)  ·  3001 Luis Romero (driver)";

    [RelayCommand]
    private async Task SignInAsync()
    {
        var code = ScanCodes.Parse(BadgeInput);
        var badge = code.Kind == ScanCodeKind.Badge ? code.Value : BadgeInput.Trim();
        BadgeInput = string.Empty;
        if (badge.Length == 0)
            return;

        SignInError = null;
        try
        {
            var session = await _client.InventoryAsync(s => s.SignInAsync(badge, Settings.StationCode));
            IsOnline = true;
            await Scan.StartAsync(session);
            Session = session;
            SelectedTab = 0;

            _ = Inventory.RefreshAsync();
            Fleet.Start();
            Machines.Start();
            _ = SyncAsync();
        }
        catch (Exception ex)
        {
            SignInError = YardTrackerClient.Describe(ex);
            if (YardTrackerClient.IsConnectivityFailure(ex))
                IsOnline = false;
        }
    }

    [RelayCommand]
    private void SignOut()
    {
        Scan.Stop();
        Fleet.Stop();
        Machines.Stop();
        Session = null;
    }

    private async Task SyncAsync()
    {
        if (_queue.Count > 0)
            await _submitter.FlushAsync();
        else if (!IsOnline)
            await _submitter.ProbeAsync();
    }
}

internal static class CollectionExtensions
{
    public static void ReplaceWith<T>(this ObservableCollection<T> collection, IEnumerable<T> items)
    {
        collection.Clear();
        foreach (var item in items)
            collection.Add(item);
    }
}
