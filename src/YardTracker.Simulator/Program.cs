using YardTracker.Client;
using YardTracker.Simulator;

const string DefaultConnection = @"Server=(localdb)\MSSQLLocalDB;Database=YardTracker;Integrated Security=true;TrustServerCertificate=true;Application Name=YardTracker.Simulator";

var options = Args.Parse(args);
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};

var service = new ServiceEndpointOptions
{
    BaseAddress = options.Get("service", "net.tcp://localhost:8523/YardTracker/"),
    Security = options.Get("security", "Transport")
};
var seed = options.GetInt("seed", 42);

try
{
    switch (options.Command)
    {
        case "backfill":
            return await new BackfillCommand(
                options.Get("connection", DefaultConnection),
                days: options.GetInt("days", 60),
                seed: seed,
                runEtl: !options.Has("no-etl")).RunAsync(cancellation.Token);

        case "traffic":
        {
            using var client = new YardTrackerClient(service);
            return await new TrafficCommand(client, options.GetDouble("rate", 1.0), options.GetDouble("retry-chance", 0.05), seed)
                .RunAsync(cancellation.Token);
        }

        case "trucks":
        {
            using var client = new YardTrackerClient(service);
            return await new TruckCommand(client, options.GetInt("trucks", 2), options.GetInt("trip-seconds", 90), options.GetDouble("ping-seconds", 2), seed)
                .RunAsync(cancellation.Token);
        }

        case "plc":
            return await PlcCommand.RunAsync(options.Get("bind", "127.0.0.1"), options.GetInt("port", 5020), options.GetDouble("speedup", 30), cancellation.Token);

        default:
            Console.WriteLine("""
                YardTracker simulator

                Usage: YardTracker.Simulator <command> [options]

                  backfill   Generate N days of realistic history directly in SQL Server
                             --days 60  --seed 42  --connection "<sql connection string>"  --no-etl

                  traffic    Live handheld scans (check in/out, moves, receipts, misreads) through WCF
                             --rate 1.0 (scans/sec)  --retry-chance 0.05  --service net.tcp://localhost:8523/YardTracker/

                  trucks     Live truck deliveries with GPS pings through WCF
                             --trucks 2  --trip-seconds 90  --ping-seconds 2

                  plc        Modbus TCP server simulating yard machines (units 1-4)
                             --bind 127.0.0.1  --port 5020  --speedup 30

                Common: --security Transport|None  --seed 42
                """);
            return options.Command.Length == 0 ? 0 : 1;
    }
}
catch (OperationCanceledException)
{
    return 0;
}
