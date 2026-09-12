using YardTracker.Client;
using YardTracker.EdgeGateway;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(options => options.ServiceName = "YardTracker Edge Gateway");
builder.Services.Configure<ModbusOptions>(builder.Configuration.GetSection("Modbus"));
builder.Services.AddSingleton(_ =>
{
    var endpoint = new ServiceEndpointOptions();
    builder.Configuration.GetSection("Service").Bind(endpoint);
    return new YardTrackerClient(endpoint);
});
builder.Services.AddHostedService<ModbusPollingWorker>();

await builder.Build().RunAsync();
