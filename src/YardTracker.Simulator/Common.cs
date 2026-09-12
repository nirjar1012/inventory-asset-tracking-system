using System.Globalization;

namespace YardTracker.Simulator;

internal sealed class Args
{
    private readonly Dictionary<string, string?> _options = new(StringComparer.OrdinalIgnoreCase);

    public string Command { get; private set; } = string.Empty;

    public static Args Parse(string[] args)
    {
        var result = new Args();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                var name = args[i][2..];
                var hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal);
                result._options[name] = hasValue ? args[++i] : null;
            }
            else if (result.Command.Length == 0)
            {
                result.Command = args[i].ToLowerInvariant();
            }
        }
        return result;
    }

    public bool Has(string name) => _options.ContainsKey(name);

    public string Get(string name, string fallback) => _options.TryGetValue(name, out var value) && value != null ? value : fallback;

    public int GetInt(string name, int fallback) => int.TryParse(Get(name, ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    public double GetDouble(string name, double fallback) => double.TryParse(Get(name, ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
}

internal static class Out
{
    private static readonly Lock Gate = new();

    public static void Info(string message) => Write(ConsoleColor.Gray, message);
    public static void Ok(string message) => Write(ConsoleColor.Green, message);
    public static void Warn(string message) => Write(ConsoleColor.Yellow, message);
    public static void Error(string message) => Write(ConsoleColor.Red, message);
    public static void Accent(string message) => Write(ConsoleColor.Cyan, message);

    public static void Write(ConsoleColor color, string message)
    {
        lock (Gate)
        {
            Console.ForegroundColor = color;
            Console.WriteLine($"{DateTime.Now:HH:mm:ss} {message}");
            Console.ResetColor();
        }
    }
}

internal static class Geo
{
    private const double EarthRadiusMiles = 3958.8;

    public static double Miles(double lat1, double lon1, double lat2, double lon2)
    {
        var dLat = ToRadians(lat2 - lat1);
        var dLon = ToRadians(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
              + Math.Cos(ToRadians(lat1)) * Math.Cos(ToRadians(lat2)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return EarthRadiusMiles * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    public static int Bearing(double lat1, double lon1, double lat2, double lon2)
    {
        var y = Math.Sin(ToRadians(lon2 - lon1)) * Math.Cos(ToRadians(lat2));
        var x = Math.Cos(ToRadians(lat1)) * Math.Sin(ToRadians(lat2))
              - Math.Sin(ToRadians(lat1)) * Math.Cos(ToRadians(lat2)) * Math.Cos(ToRadians(lon2 - lon1));
        return ((int)Math.Round(Math.Atan2(y, x) * 180 / Math.PI) + 360) % 360;
    }

    /// <summary>Point at fraction <paramref name="t"/> along a gently curved route, so a plotted trip looks like a road rather than a ruler line.</summary>
    public static (double Lat, double Lon) Along(double lat1, double lon1, double lat2, double lon2, double t, double bend)
    {
        var lat = lat1 + (lat2 - lat1) * t;
        var lon = lon1 + (lon2 - lon1) * t;
        var offset = Math.Sin(Math.PI * t) * bend;
        return (lat - (lon2 - lon1) * offset, lon + (lat2 - lat1) * offset);
    }

    private static double ToRadians(double degrees) => degrees * Math.PI / 180;
}

internal sealed record ProductMix(string ProductCode, string Category, int MinPieces, int MaxPieces);

/// <summary>Simulation knowledge about the seeded yard (database/04_seed.sql).</summary>
internal static class Catalog
{
    public const string DriverStation = "HH-DRV1";
    public static readonly string[] DriverBadges = ["3001", "3002"];
    public static readonly string[] Sites = ["NYD", "SYD", "CTP"];

    public static readonly (string Code, decimal PayloadLbs)[] Trucks = [("TRK-101", 48000), ("TRK-102", 48000), ("TRK-201", 45000)];

    public static readonly Dictionary<string, ProductMix> Products = new ProductMix[]
    {
        new("PIPE-X52-1275", "Pipe", 3, 8),
        new("PIPE-X65-1600", "Pipe", 2, 5),
        new("PIPE-B-0663", "Pipe", 10, 30),
        new("CSG-P110-0700", "Casing", 8, 20),
        new("CSG-J55-0963", "Casing", 6, 15),
        new("TBG-L80-0288", "Tubing", 30, 80),
        new("TBG-N80-0350", "Tubing", 25, 60),
        new("PLT-A36-0500", "Plate", 1, 4),
        new("BM-W12X26", "Beam", 4, 12),
        new("BAR-1018-0200", "Bar", 20, 60)
    }.ToDictionary(p => p.ProductCode);

    private static readonly Dictionary<string, (string Value, double Weight)[]> ReceiptMix = new()
    {
        ["NYD"] = [("PIPE-X52-1275", 22), ("PIPE-X65-1600", 12), ("PIPE-B-0663", 16), ("CSG-P110-0700", 18), ("CSG-J55-0963", 12), ("TBG-L80-0288", 12), ("TBG-N80-0350", 8)],
        ["SYD"] = [("PLT-A36-0500", 35), ("BM-W12X26", 35), ("BAR-1018-0200", 30)],
        ["CTP"] = [("PIPE-X52-1275", 50), ("CSG-P110-0700", 50)]
    };

    public static ProductMix PickProduct(this Random rng, string site) => Products[rng.PickWeighted(ReceiptMix[site])];

    public static string[] StorageFor(string site, string category) => (site, category) switch
    {
        ("NYD", "Pipe") => ["NYD-R01", "NYD-R02", "NYD-LD-A", "NYD-LD-B"],
        ("NYD", "Casing") => ["NYD-R03", "NYD-LD-B"],
        ("NYD", "Tubing") => ["NYD-R04"],
        ("NYD", _) => ["NYD-LD-A"],
        ("SYD", "Plate" or "Beam") => ["SYD-R01", "SYD-LD-A"],
        ("SYD", "Bar") => ["SYD-R02"],
        ("SYD", _) => ["SYD-LD-A"],
        _ => ["CTP-FG"]
    };

    /// <summary>Racks, laydown areas and finished goods: where stock sits waiting to be used.</summary>
    public static bool IsStorage(string? location) =>
        location != null
        && !location.EndsWith("-DOCK1", StringComparison.Ordinal)
        && !location.EndsWith("-QA", StringComparison.Ordinal)
        && !location.EndsWith("-STG", StringComparison.Ordinal)
        && !location.StartsWith("CTP-BAY", StringComparison.Ordinal);

    public static string SiteOf(string locationCode) => locationCode[..3];

    public static (string Station, string Badge) Crew(this Random rng, string site) => site switch
    {
        "NYD" => rng.Next(3) switch
        {
            0 => ("HH-01", "1001"),
            1 => ("HH-01", "1005"),
            _ => ("HH-02", "1002")
        },
        "SYD" => ("HH-03", "1003"),
        _ => ("HH-04", "1004")
    };

    public static string NewTag(this Random rng, int sequence) =>
        rng.NextDouble() < 0.3
            ? "E28011606000" + rng.NextInt64(0, 0xFFFFFFFFFFFF).ToString("X12")   // 96-bit RFID EPC
            : $"YT-{sequence:D6}";                                                 // printed barcode label

    public static string HeatNumber(this Random rng) => $"{(char)('A' + rng.Next(26))}{rng.Next(10000, 99999)}";

    public static T Pick<T>(this Random rng, IReadOnlyList<T> items) => items[rng.Next(items.Count)];

    public static string PickWeighted(this Random rng, (string Value, double Weight)[] options)
    {
        var roll = rng.NextDouble() * options.Sum(o => o.Weight);
        foreach (var (value, weight) in options)
        {
            roll -= weight;
            if (roll <= 0)
                return value;
        }
        return options[^1].Value;
    }
}
