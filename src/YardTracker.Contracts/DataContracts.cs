using System;
using System.Runtime.Serialization;

namespace YardTracker.Contracts
{
    [DataContract(Namespace = ContractNamespaces.Data)]
    public class ServiceFault
    {
        /// <summary>Machine-readable code: Validation, Rejected, Unavailable, Internal.</summary>
        [DataMember] public string Code { get; set; } = string.Empty;
        [DataMember] public string Message { get; set; } = string.Empty;
        /// <summary>Matches the service log entry, so support can find the details.</summary>
        [DataMember] public string CorrelationId { get; set; } = string.Empty;
    }

    [DataContract(Namespace = ContractNamespaces.Data)]
    public enum ScanOutcome
    {
        [EnumMember] Accepted,
        /// <summary>The same ClientScanId was already processed (a retry). Nothing changed.</summary>
        [EnumMember] Duplicate,
        [EnumMember] Rejected
    }

    // ------------------------------------------------------------------ inventory

    [DataContract(Namespace = ContractNamespaces.Data)]
    public class StationSession
    {
        [DataMember] public string BadgeNumber { get; set; } = string.Empty;
        [DataMember] public string OperatorName { get; set; } = string.Empty;
        [DataMember] public string Role { get; set; } = string.Empty;
        [DataMember] public string StationCode { get; set; } = string.Empty;
        [DataMember] public string StationName { get; set; } = string.Empty;
        [DataMember] public string DeviceType { get; set; } = string.Empty;
        [DataMember] public string? DefaultLocationCode { get; set; }
        [DataMember] public DateTime ServerTimeUtc { get; set; }
    }

    [DataContract(Namespace = ContractNamespaces.Data)]
    public class SiteInfo
    {
        [DataMember] public string SiteCode { get; set; } = string.Empty;
        [DataMember] public string Name { get; set; } = string.Empty;
        [DataMember] public double Latitude { get; set; }
        [DataMember] public double Longitude { get; set; }
    }

    [DataContract(Namespace = ContractNamespaces.Data)]
    public class LocationInfo
    {
        [DataMember] public string LocationCode { get; set; } = string.Empty;
        [DataMember] public string Name { get; set; } = string.Empty;
        [DataMember] public string LocationType { get; set; } = string.Empty;
        [DataMember] public string SiteCode { get; set; } = string.Empty;
        [DataMember] public string SiteName { get; set; } = string.Empty;
        [DataMember] public decimal? CapacityTons { get; set; }

        public string DisplayName => $"{LocationCode}  {Name}";
    }

    [DataContract(Namespace = ContractNamespaces.Data)]
    public class ProductInfo
    {
        [DataMember] public string ProductCode { get; set; } = string.Empty;
        [DataMember] public string Category { get; set; } = string.Empty;
        [DataMember] public string Description { get; set; } = string.Empty;
        [DataMember] public string Grade { get; set; } = string.Empty;
        [DataMember] public decimal NominalLengthFt { get; set; }
        [DataMember] public decimal WeightLbsPerFt { get; set; }

        public string DisplayName => $"{ProductCode}  {Description}";
    }

    [DataContract(Namespace = ContractNamespaces.Data)]
    public class ItemInfo
    {
        [DataMember] public string TagId { get; set; } = string.Empty;
        [DataMember] public string ProductCode { get; set; } = string.Empty;
        [DataMember] public string Category { get; set; } = string.Empty;
        [DataMember] public string Description { get; set; } = string.Empty;
        [DataMember] public string Grade { get; set; } = string.Empty;
        [DataMember] public string HeatNumber { get; set; } = string.Empty;
        [DataMember] public int Pieces { get; set; }
        [DataMember] public decimal TotalLengthFt { get; set; }
        [DataMember] public decimal WeightLbs { get; set; }
        /// <summary>InYard, CheckedOut or InTransit.</summary>
        [DataMember] public string Status { get; set; } = string.Empty;
        [DataMember] public string? LocationCode { get; set; }
        [DataMember] public string? LocationName { get; set; }
        [DataMember] public string? SiteCode { get; set; }
        [DataMember] public DateTime LastMovedAtUtc { get; set; }
        [DataMember] public int DaysSinceLastMove { get; set; }
    }

    [DataContract(Namespace = ContractNamespaces.Data)]
    public class ItemSearch
    {
        [DataMember] public string? Status { get; set; }
        [DataMember] public string? LocationCode { get; set; }
        [DataMember] public string? SiteCode { get; set; }
        [DataMember] public int Top { get; set; } = 100;
    }

    /// <summary>A single check-in, check-out or move scan.</summary>
    [DataContract(Namespace = ContractNamespaces.Data)]
    public class ScanRequest
    {
        /// <summary>Generated on the device when the tag is read. Retries reuse it, which makes the call idempotent.</summary>
        [DataMember] public Guid ClientScanId { get; set; }
        [DataMember] public string TagId { get; set; } = string.Empty;
        /// <summary>Target location for check-in and move. Ignored for check-out.</summary>
        [DataMember] public string? LocationCode { get; set; }
        [DataMember] public string OperatorBadge { get; set; } = string.Empty;
        [DataMember] public string StationCode { get; set; } = string.Empty;
        /// <summary>Device clock at the time of the read. May be well in the past for scans queued offline.</summary>
        [DataMember] public DateTime ScannedAtUtc { get; set; }
        /// <summary>Work order, BOL or PO number.</summary>
        [DataMember] public string? Reference { get; set; }
    }

    [DataContract(Namespace = ContractNamespaces.Data)]
    public class ReceiveRequest
    {
        [DataMember] public Guid ClientScanId { get; set; }
        [DataMember] public string TagId { get; set; } = string.Empty;
        [DataMember] public string ProductCode { get; set; } = string.Empty;
        [DataMember] public string HeatNumber { get; set; } = string.Empty;
        [DataMember] public int Pieces { get; set; }
        [DataMember] public decimal? TotalLengthFt { get; set; }
        [DataMember] public string LocationCode { get; set; } = string.Empty;
        [DataMember] public string OperatorBadge { get; set; } = string.Empty;
        [DataMember] public string StationCode { get; set; } = string.Empty;
        [DataMember] public DateTime ScannedAtUtc { get; set; }
        [DataMember] public string? Reference { get; set; }
    }

    [DataContract(Namespace = ContractNamespaces.Data)]
    public class ScanResult
    {
        [DataMember] public ScanOutcome Outcome { get; set; }
        /// <summary>Set when rejected: UnknownTag, InvalidState, WrongSite, ...</summary>
        [DataMember] public string? ExceptionType { get; set; }
        [DataMember] public string Message { get; set; } = string.Empty;
        [DataMember] public long? ScanEventId { get; set; }
        [DataMember] public string ScannedTag { get; set; } = string.Empty;
        [DataMember] public ItemInfo? Item { get; set; }
    }

    [DataContract(Namespace = ContractNamespaces.Data)]
    public class ScanEventInfo
    {
        [DataMember] public long ScanEventId { get; set; }
        [DataMember] public string EventType { get; set; } = string.Empty;
        [DataMember] public string TagId { get; set; } = string.Empty;
        [DataMember] public string? FromLocationCode { get; set; }
        [DataMember] public string? ToLocationCode { get; set; }
        [DataMember] public string OperatorName { get; set; } = string.Empty;
        [DataMember] public string? StationCode { get; set; }
        [DataMember] public int? DeliveryId { get; set; }
        [DataMember] public string? Reference { get; set; }
        [DataMember] public int Pieces { get; set; }
        [DataMember] public decimal WeightLbs { get; set; }
        [DataMember] public DateTime ScannedAtUtc { get; set; }
        [DataMember] public DateTime ReceivedAtUtc { get; set; }
    }

    [DataContract(Namespace = ContractNamespaces.Data)]
    public class LocationInventory
    {
        [DataMember] public string SiteCode { get; set; } = string.Empty;
        [DataMember] public string LocationCode { get; set; } = string.Empty;
        [DataMember] public string LocationName { get; set; } = string.Empty;
        [DataMember] public string LocationType { get; set; } = string.Empty;
        [DataMember] public decimal? CapacityTons { get; set; }
        [DataMember] public int ItemCount { get; set; }
        [DataMember] public int Pieces { get; set; }
        [DataMember] public decimal WeightTons { get; set; }
        [DataMember] public decimal? UtilizationPct { get; set; }
        [DataMember] public DateTime? OldestMoveAtUtc { get; set; }
    }

    [DataContract(Namespace = ContractNamespaces.Data)]
    public class ScanExceptionInfo
    {
        [DataMember] public long ScanExceptionId { get; set; }
        [DataMember] public DateTime OccurredAtUtc { get; set; }
        [DataMember] public string ExceptionType { get; set; } = string.Empty;
        [DataMember] public string AttemptedAction { get; set; } = string.Empty;
        [DataMember] public string RawTag { get; set; } = string.Empty;
        [DataMember] public string Message { get; set; } = string.Empty;
        [DataMember] public string? OperatorName { get; set; }
        [DataMember] public string? StationCode { get; set; }
        [DataMember] public string? LocationCode { get; set; }
    }

    // ------------------------------------------------------------------ logistics

    [DataContract(Namespace = ContractNamespaces.Data)]
    public class StartDeliveryRequest
    {
        [DataMember] public Guid ClientRequestId { get; set; }
        [DataMember] public string TruckCode { get; set; } = string.Empty;
        [DataMember] public string DriverBadge { get; set; } = string.Empty;
        [DataMember] public string FromSiteCode { get; set; } = string.Empty;
        [DataMember] public string ToSiteCode { get; set; } = string.Empty;
        [DataMember] public string[] TagIds { get; set; } = Array.Empty<string>();
        [DataMember] public string? StationCode { get; set; }
        [DataMember] public DateTime? DepartedAtUtc { get; set; }
    }

    [DataContract(Namespace = ContractNamespaces.Data)]
    public class CompleteDeliveryRequest
    {
        [DataMember] public int DeliveryId { get; set; }
        [DataMember] public string ArrivalLocationCode { get; set; } = string.Empty;
        [DataMember] public string OperatorBadge { get; set; } = string.Empty;
        [DataMember] public string? StationCode { get; set; }
        [DataMember] public DateTime? ArrivedAtUtc { get; set; }
    }

    [DataContract(Namespace = ContractNamespaces.Data)]
    public class DeliveryResult
    {
        [DataMember] public ScanOutcome Outcome { get; set; }
        [DataMember] public string Message { get; set; } = string.Empty;
        [DataMember] public int? DeliveryId { get; set; }
        [DataMember] public int ItemCount { get; set; }
        [DataMember] public decimal WeightLbs { get; set; }
    }

    [DataContract(Namespace = ContractNamespaces.Data)]
    public class GpsPosition
    {
        [DataMember] public string TruckCode { get; set; } = string.Empty;
        [DataMember] public double Latitude { get; set; }
        [DataMember] public double Longitude { get; set; }
        [DataMember] public double? SpeedMph { get; set; }
        [DataMember] public int? HeadingDeg { get; set; }
        [DataMember] public DateTime RecordedAtUtc { get; set; }
    }

    [DataContract(Namespace = ContractNamespaces.Data)]
    public class TruckStatus
    {
        [DataMember] public string TruckCode { get; set; } = string.Empty;
        [DataMember] public string Description { get; set; } = string.Empty;
        [DataMember] public int? DeliveryId { get; set; }
        [DataMember] public string? FromSiteCode { get; set; }
        [DataMember] public string? ToSiteCode { get; set; }
        [DataMember] public string? DriverName { get; set; }
        [DataMember] public DateTime? DepartedAtUtc { get; set; }
        [DataMember] public int ItemCount { get; set; }
        [DataMember] public double? Latitude { get; set; }
        [DataMember] public double? Longitude { get; set; }
        [DataMember] public double? SpeedMph { get; set; }
        [DataMember] public int? HeadingDeg { get; set; }
        [DataMember] public DateTime? LastPingAtUtc { get; set; }
    }

    // ------------------------------------------------------------------ telemetry

    [DataContract(Namespace = ContractNamespaces.Data)]
    public class MachineInfo
    {
        [DataMember] public string MachineCode { get; set; } = string.Empty;
        [DataMember] public string Name { get; set; } = string.Empty;
        [DataMember] public string MachineType { get; set; } = string.Empty;
        [DataMember] public string LocationCode { get; set; } = string.Empty;
        [DataMember] public byte ModbusUnitId { get; set; }
        [DataMember] public decimal? TempWarnC { get; set; }
        [DataMember] public decimal? TempAlarmC { get; set; }
    }

    [DataContract(Namespace = ContractNamespaces.Data)]
    public class MachineReading
    {
        [DataMember] public string MachineCode { get; set; } = string.Empty;
        [DataMember] public DateTime RecordedAtUtc { get; set; }
        [DataMember] public decimal? TemperatureC { get; set; }
        [DataMember] public short StatusCode { get; set; }
        [DataMember] public long? CycleCount { get; set; }
        [DataMember] public short FaultCode { get; set; }
    }

    [DataContract(Namespace = ContractNamespaces.Data)]
    public class MachineStatus
    {
        [DataMember] public string MachineCode { get; set; } = string.Empty;
        [DataMember] public string Name { get; set; } = string.Empty;
        [DataMember] public string MachineType { get; set; } = string.Empty;
        [DataMember] public string LocationCode { get; set; } = string.Empty;
        [DataMember] public short? StatusCode { get; set; }
        [DataMember] public string StatusName { get; set; } = string.Empty;
        [DataMember] public decimal? TemperatureC { get; set; }
        [DataMember] public decimal? TempWarnC { get; set; }
        [DataMember] public decimal? TempAlarmC { get; set; }
        [DataMember] public long? CycleCount { get; set; }
        [DataMember] public short? FaultCode { get; set; }
        [DataMember] public DateTime? LastReadingAtUtc { get; set; }
    }
}
