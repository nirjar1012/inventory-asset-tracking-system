using System;
using System.Data;
using YardTracker.Contracts;

namespace YardTracker.Service.Data
{
    /// <summary>Maps stored procedure result columns to data contracts.</summary>
    internal static class Mapping
    {
        public static StationSession ToSession(IDataRecord r) => new StationSession
        {
            BadgeNumber = r.Str("BadgeNumber"),
            OperatorName = r.Str("DisplayName"),
            Role = r.Str("Role"),
            StationCode = r.Str("StationCode"),
            StationName = r.Str("StationName"),
            DeviceType = r.Str("DeviceType"),
            DefaultLocationCode = r.StrOrNull("DefaultLocationCode"),
            ServerTimeUtc = r.Utc("ServerTimeUtc")
        };

        public static SiteInfo ToSite(IDataRecord r) => new SiteInfo
        {
            SiteCode = r.Str("SiteCode"),
            Name = r.Str("Name"),
            Latitude = r.Dbl("Latitude"),
            Longitude = r.Dbl("Longitude")
        };

        public static LocationInfo ToLocation(IDataRecord r) => new LocationInfo
        {
            LocationCode = r.Str("LocationCode"),
            Name = r.Str("Name"),
            LocationType = r.Str("LocationType"),
            SiteCode = r.Str("SiteCode"),
            SiteName = r.Str("SiteName"),
            CapacityTons = r.DecOrNull("CapacityTons")
        };

        public static ProductInfo ToProduct(IDataRecord r) => new ProductInfo
        {
            ProductCode = r.Str("ProductCode"),
            Category = r.Str("Category"),
            Description = r.Str("Description"),
            Grade = r.Str("Grade"),
            NominalLengthFt = r.Dec("NominalLengthFt"),
            WeightLbsPerFt = r.Dec("WeightLbsPerFt")
        };

        /// <summary>Columns of dbo.vw_ItemDetails.</summary>
        public static ItemInfo ToItem(IDataRecord r) => new ItemInfo
        {
            TagId = r.Str("TagId"),
            ProductCode = r.Str("ProductCode"),
            Category = r.Str("Category"),
            Description = r.Str("Description"),
            Grade = r.Str("Grade"),
            HeatNumber = r.Str("HeatNumber"),
            Pieces = r.Int("Pieces"),
            TotalLengthFt = r.Dec("TotalLengthFt"),
            WeightLbs = r.Dec("WeightLbs"),
            Status = r.Str("Status"),
            LocationCode = r.StrOrNull("LocationCode"),
            LocationName = r.StrOrNull("LocationName"),
            SiteCode = r.StrOrNull("SiteCode"),
            LastMovedAtUtc = r.Utc("LastMovedAtUtc"),
            DaysSinceLastMove = r.Int("DaysSinceLastMove")
        };

        public static ScanResult ToScanResult(IDataRecord r) => new ScanResult
        {
            Outcome = (ScanOutcome)Enum.Parse(typeof(ScanOutcome), r.Str("Outcome")),
            ExceptionType = r.StrOrNull("ExceptionType"),
            Message = r.Str("Message"),
            ScanEventId = r.LongOrNull("ScanEventId"),
            ScannedTag = r.Str("ScannedTag"),
            Item = r["ItemId"] is DBNull ? null : ToItem(r)
        };

        public static ScanEventInfo ToScanEvent(IDataRecord r) => new ScanEventInfo
        {
            ScanEventId = r.LongOrNull("ScanEventId") ?? 0,
            EventType = r.Str("EventType"),
            TagId = r.Str("TagId"),
            FromLocationCode = r.StrOrNull("FromLocationCode"),
            ToLocationCode = r.StrOrNull("ToLocationCode"),
            OperatorName = r.Str("OperatorName"),
            StationCode = r.StrOrNull("StationCode"),
            DeliveryId = r.IntOrNull("DeliveryId"),
            Reference = r.StrOrNull("Reference"),
            Pieces = r.Int("Pieces"),
            WeightLbs = r.Dec("WeightLbs"),
            ScannedAtUtc = r.Utc("ScannedAtUtc"),
            ReceivedAtUtc = r.Utc("ReceivedAtUtc")
        };

        public static LocationInventory ToLocationInventory(IDataRecord r) => new LocationInventory
        {
            SiteCode = r.Str("SiteCode"),
            LocationCode = r.Str("LocationCode"),
            LocationName = r.Str("LocationName"),
            LocationType = r.Str("LocationType"),
            CapacityTons = r.DecOrNull("CapacityTons"),
            ItemCount = r.Int("ItemCount"),
            Pieces = r.Int("Pieces"),
            WeightTons = r.Dec("WeightTons"),
            UtilizationPct = r.DecOrNull("UtilizationPct"),
            OldestMoveAtUtc = r.UtcOrNull("OldestMoveAtUtc")
        };

        public static ScanExceptionInfo ToScanException(IDataRecord r) => new ScanExceptionInfo
        {
            ScanExceptionId = r.LongOrNull("ScanExceptionId") ?? 0,
            OccurredAtUtc = r.Utc("OccurredAtUtc"),
            ExceptionType = r.Str("ExceptionType"),
            AttemptedAction = r.Str("AttemptedAction"),
            RawTag = r.Str("RawTag"),
            Message = r.Str("Message"),
            OperatorName = r.StrOrNull("OperatorName"),
            StationCode = r.StrOrNull("StationCode"),
            LocationCode = r.StrOrNull("LocationCode")
        };

        public static DeliveryResult ToDeliveryResult(IDataRecord r) => new DeliveryResult
        {
            Outcome = (ScanOutcome)Enum.Parse(typeof(ScanOutcome), r.Str("Outcome")),
            Message = r.Str("Message"),
            DeliveryId = r.IntOrNull("DeliveryId"),
            ItemCount = r.IntOrNull("ItemCount") ?? 0,
            WeightLbs = r.Dec("WeightLbs")
        };

        public static TruckStatus ToTruckStatus(IDataRecord r) => new TruckStatus
        {
            TruckCode = r.Str("TruckCode"),
            Description = r.Str("Description"),
            DeliveryId = r.IntOrNull("DeliveryId"),
            FromSiteCode = r.StrOrNull("FromSiteCode"),
            ToSiteCode = r.StrOrNull("ToSiteCode"),
            DriverName = r.StrOrNull("DriverName"),
            DepartedAtUtc = r.UtcOrNull("DepartedAtUtc"),
            ItemCount = r.IntOrNull("ItemCount") ?? 0,
            Latitude = r.DblOrNull("Latitude"),
            Longitude = r.DblOrNull("Longitude"),
            SpeedMph = r.DblOrNull("SpeedMph"),
            HeadingDeg = r.IntOrNull("HeadingDeg"),
            LastPingAtUtc = r.UtcOrNull("LastPingAtUtc")
        };

        public static GpsPosition ToGpsPosition(IDataRecord r) => new GpsPosition
        {
            TruckCode = r.Str("TruckCode"),
            Latitude = r.Dbl("Latitude"),
            Longitude = r.Dbl("Longitude"),
            SpeedMph = r.DblOrNull("SpeedMph"),
            HeadingDeg = r.IntOrNull("HeadingDeg"),
            RecordedAtUtc = r.Utc("RecordedAtUtc")
        };

        public static MachineInfo ToMachine(IDataRecord r) => new MachineInfo
        {
            MachineCode = r.Str("MachineCode"),
            Name = r.Str("Name"),
            MachineType = r.Str("MachineType"),
            LocationCode = r.Str("LocationCode"),
            ModbusUnitId = r.Byte("ModbusUnitId"),
            TempWarnC = r.DecOrNull("TempWarnC"),
            TempAlarmC = r.DecOrNull("TempAlarmC")
        };

        public static MachineStatus ToMachineStatus(IDataRecord r) => new MachineStatus
        {
            MachineCode = r.Str("MachineCode"),
            Name = r.Str("Name"),
            MachineType = r.Str("MachineType"),
            LocationCode = r.Str("LocationCode"),
            StatusCode = r.ShortOrNull("StatusCode"),
            StatusName = r.Str("StatusName"),
            TemperatureC = r.DecOrNull("TemperatureC"),
            TempWarnC = r.DecOrNull("TempWarnC"),
            TempAlarmC = r.DecOrNull("TempAlarmC"),
            CycleCount = r.LongOrNull("CycleCount"),
            FaultCode = r.ShortOrNull("FaultCode"),
            LastReadingAtUtc = r.UtcOrNull("LastReadingAtUtc")
        };
    }
}
