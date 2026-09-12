using System.ServiceModel;
using System.Threading.Tasks;

namespace YardTracker.Contracts
{
    public static class ContractNamespaces
    {
        public const string Service = "urn:yardtracker:services:2026-09";
        public const string Data = "urn:yardtracker:data:2026-09";
    }

    /// <summary>
    /// Operations used by scanning stations (handhelds, RFID portals, kiosks).
    /// A rejected scan is a normal result (<see cref="ScanOutcome.Rejected"/>), not a fault.
    /// Faults are reserved for invalid requests and infrastructure failures.
    /// </summary>
    [ServiceContract(Name = "InventoryService", Namespace = ContractNamespaces.Service)]
    public interface IInventoryService
    {
        [OperationContract, FaultContract(typeof(ServiceFault))]
        Task<StationSession> SignInAsync(string badgeNumber, string stationCode);

        [OperationContract, FaultContract(typeof(ServiceFault))]
        Task<LocationInfo[]> GetLocationsAsync(string? siteCode);

        [OperationContract, FaultContract(typeof(ServiceFault))]
        Task<ProductInfo[]> GetProductsAsync();

        [OperationContract, FaultContract(typeof(ServiceFault))]
        Task<ItemInfo?> LookupItemAsync(string tagId);

        [OperationContract, FaultContract(typeof(ServiceFault))]
        Task<ItemInfo[]> SearchItemsAsync(ItemSearch search);

        [OperationContract, FaultContract(typeof(ServiceFault))]
        Task<ScanResult> ReceiveItemAsync(ReceiveRequest request);

        [OperationContract, FaultContract(typeof(ServiceFault))]
        Task<ScanResult> CheckInItemAsync(ScanRequest request);

        [OperationContract, FaultContract(typeof(ServiceFault))]
        Task<ScanResult> CheckOutItemAsync(ScanRequest request);

        [OperationContract, FaultContract(typeof(ServiceFault))]
        Task<ScanResult> MoveItemAsync(ScanRequest request);

        [OperationContract, FaultContract(typeof(ServiceFault))]
        Task<ScanEventInfo[]> GetItemHistoryAsync(string tagId);

        [OperationContract, FaultContract(typeof(ServiceFault))]
        Task<ScanEventInfo[]> GetRecentScansAsync(string? stationCode, int top);

        [OperationContract, FaultContract(typeof(ServiceFault))]
        Task<LocationInventory[]> GetInventoryByLocationAsync(string? siteCode);

        [OperationContract, FaultContract(typeof(ServiceFault))]
        Task<ScanExceptionInfo[]> GetOpenExceptionsAsync(int top);
    }

    /// <summary>Truck deliveries between sites and GPS tracking.</summary>
    [ServiceContract(Name = "LogisticsService", Namespace = ContractNamespaces.Service)]
    public interface ILogisticsService
    {
        [OperationContract, FaultContract(typeof(ServiceFault))]
        Task<SiteInfo[]> GetSitesAsync();

        [OperationContract, FaultContract(typeof(ServiceFault))]
        Task<DeliveryResult> StartDeliveryAsync(StartDeliveryRequest request);

        [OperationContract, FaultContract(typeof(ServiceFault))]
        Task<DeliveryResult> CompleteDeliveryAsync(CompleteDeliveryRequest request);

        [OperationContract, FaultContract(typeof(ServiceFault))]
        Task ReportPositionAsync(GpsPosition position);

        [OperationContract, FaultContract(typeof(ServiceFault))]
        Task<TruckStatus[]> GetFleetStatusAsync();

        [OperationContract, FaultContract(typeof(ServiceFault))]
        Task<GpsPosition[]> GetDeliveryRouteAsync(int deliveryId);
    }

    /// <summary>Machine telemetry pushed by the Modbus TCP edge gateway.</summary>
    [ServiceContract(Name = "TelemetryService", Namespace = ContractNamespaces.Service)]
    public interface ITelemetryService
    {
        [OperationContract, FaultContract(typeof(ServiceFault))]
        Task<MachineInfo[]> GetMachinesAsync();

        [OperationContract, FaultContract(typeof(ServiceFault))]
        Task<int> RecordReadingsAsync(MachineReading[] readings);

        [OperationContract, FaultContract(typeof(ServiceFault))]
        Task<MachineStatus[]> GetMachineStatusAsync();
    }
}
