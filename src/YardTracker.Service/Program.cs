using System;
using System.ServiceModel;
using System.Threading;
using YardTracker.Service.Data;

namespace YardTracker.Service
{
    internal static class Program
    {
        private static int Main()
        {
            Console.Title = "YardTracker WCF Service";

            using (var stop = new ManualResetEventSlim())
            {
                Console.CancelKeyPress += (_, e) =>
                {
                    e.Cancel = true;
                    stop.Set();
                };

                var host = new ServiceHost(typeof(YardTrackerService));
                host.Description.Behaviors.Add(new UnhandledErrorLogger());

                try
                {
                    Log.Info("Checking database connection...");
                    Log.Info("Database: " + Db.Default.Describe());

                    host.Open();
                    foreach (var endpoint in host.Description.Endpoints)
                        Log.Info($"Listening on {endpoint.Address.Uri}  [{endpoint.Contract.Name}]");

                    Log.Ok("YardTracker service is running. Press Ctrl+C to stop.");
                    stop.Wait();

                    Log.Info("Stopping...");
                    host.Close(TimeSpan.FromSeconds(10));
                    return 0;
                }
                catch (Exception ex)
                {
                    Log.Error("Service failed", ex);
                    host.Abort();
                    return 1;
                }
            }
        }
    }
}
