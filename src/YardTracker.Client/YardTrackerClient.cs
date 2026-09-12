using System.Net.Sockets;
using System.ServiceModel;
using YardTracker.Contracts;

namespace YardTracker.Client;

public sealed class ServiceEndpointOptions
{
    public string BaseAddress { get; set; } = "net.tcp://localhost:8523/YardTracker/";

    /// <summary>"Transport" (Windows credentials, matches the service default) or "None".</summary>
    public string Security { get; set; } = "Transport";

    public int TimeoutSeconds { get; set; } = 15;
}

/// <summary>
/// Creates one <see cref="ChannelFactory{TChannel}"/> per contract (expensive, cached) and a new channel per call
/// (cheap). A faulted channel is aborted and never reused, which is the main pitfall with WCF clients.
/// </summary>
public sealed class YardTrackerClient : IDisposable
{
    private readonly ChannelFactory<IInventoryService> _inventory;
    private readonly ChannelFactory<ILogisticsService> _logistics;
    private readonly ChannelFactory<ITelemetryService> _telemetry;

    public YardTrackerClient(ServiceEndpointOptions options)
    {
        Options = options;
        var binding = CreateBinding(options);
        var baseUri = new Uri(options.BaseAddress.EndsWith('/') ? options.BaseAddress : options.BaseAddress + "/");

        _inventory = new ChannelFactory<IInventoryService>(binding, new EndpointAddress(new Uri(baseUri, "Inventory")));
        _logistics = new ChannelFactory<ILogisticsService>(binding, new EndpointAddress(new Uri(baseUri, "Logistics")));
        _telemetry = new ChannelFactory<ITelemetryService>(binding, new EndpointAddress(new Uri(baseUri, "Telemetry")));
    }

    public ServiceEndpointOptions Options { get; }

    public Task<T> InventoryAsync<T>(Func<IInventoryService, Task<T>> call) => CallAsync(_inventory, call);

    public Task<T> LogisticsAsync<T>(Func<ILogisticsService, Task<T>> call) => CallAsync(_logistics, call);

    public Task LogisticsAsync(Func<ILogisticsService, Task> call) => CallAsync(_logistics, async c =>
    {
        await call(c);
        return true;
    });

    public Task<T> TelemetryAsync<T>(Func<ITelemetryService, Task<T>> call) => CallAsync(_telemetry, call);

    /// <summary>True when the call failed because the service could not be reached, as opposed to a fault it returned.</summary>
    public static bool IsConnectivityFailure(Exception exception) => exception switch
    {
        FaultException => false,
        CommunicationException or TimeoutException or SocketException => true,
        AggregateException aggregate => aggregate.InnerExceptions.Any(IsConnectivityFailure),
        _ => false
    };

    /// <summary>User-facing message for a failed call.</summary>
    public static string Describe(Exception exception) => exception switch
    {
        FaultException<ServiceFault> fault => fault.Detail.Message,
        FaultException fault => fault.Message,
        _ when IsConnectivityFailure(exception) => "Service unreachable: " + exception.Message,
        _ => exception.Message
    };

    private static async Task<T> CallAsync<TChannel, T>(ChannelFactory<TChannel> factory, Func<TChannel, Task<T>> call)
    {
        var channel = factory.CreateChannel();
        var communicationObject = (ICommunicationObject)channel!;
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
    }

    private static NetTcpBinding CreateBinding(ServiceEndpointOptions options)
    {
        var mode = string.Equals(options.Security, "None", StringComparison.OrdinalIgnoreCase)
            ? SecurityMode.None
            : SecurityMode.Transport;

        var binding = new NetTcpBinding(mode)
        {
            OpenTimeout = TimeSpan.FromSeconds(5),
            SendTimeout = TimeSpan.FromSeconds(options.TimeoutSeconds),
            ReceiveTimeout = TimeSpan.FromSeconds(options.TimeoutSeconds),
            CloseTimeout = TimeSpan.FromSeconds(5),
            MaxReceivedMessageSize = 4 * 1024 * 1024
        };
        binding.ReaderQuotas.MaxArrayLength = 1024 * 1024;
        binding.ReaderQuotas.MaxStringContentLength = 1024 * 1024;

        if (mode == SecurityMode.Transport)
            binding.Security.Transport.ClientCredentialType = TcpClientCredentialType.Windows;

        return binding;
    }

    public void Dispose()
    {
        foreach (var factory in new ICommunicationObject[] { _inventory, _logistics, _telemetry })
        {
            try
            {
                if (factory.State == CommunicationState.Faulted)
                    factory.Abort();
                else
                    factory.Close();
            }
            catch
            {
                factory.Abort();
            }
        }
    }
}
