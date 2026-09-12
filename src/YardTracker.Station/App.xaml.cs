using System.Windows;
using YardTracker.Client;
using YardTracker.Station.Services;
using YardTracker.Station.ViewModels;

namespace YardTracker.Station;

public partial class App : Application
{
    private YardTrackerClient? _client;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "YardTracker station", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        // station.json next to the exe; override per instance: --station HH-02 --service net.tcp://host:8523/YardTracker/
        var settings = StationSettings.Load(e.Args);
        _client = new YardTrackerClient(settings.Service);
        var queue = new OfflineScanQueue(settings.StationCode);

        var window = new MainWindow { DataContext = new ShellViewModel(settings, _client, queue) };
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _client?.Dispose();
        base.OnExit(e);
    }
}
