using System;
using System.Text;
using System.Text.RegularExpressions;

namespace YardTracker.Contracts
{
    public enum ScanCodeKind
    {
        Empty,
        /// <summary>Barcode label (e.g. YT-004211) or 96-bit RFID EPC (24 hex characters).</summary>
        ItemTag,
        /// <summary>Location label, printed as LOC:NYD-R01 on rack end caps.</summary>
        Location,
        /// <summary>Operator badge, printed as BADGE:1001.</summary>
        Badge,
        Invalid
    }

    public readonly struct ScanCode
    {
        public ScanCode(ScanCodeKind kind, string value)
        {
            Kind = kind;
            Value = value;
        }

        public ScanCodeKind Kind { get; }
        public string Value { get; }

        public override string ToString() => $"{Kind}:{Value}";
    }

    /// <summary>
    /// Interprets raw scanner input. Keyboard-wedge barcode scanners and RFID readers deliver
    /// the same text a person would type, often with a trailing CR/LF or GS1 separators.
    /// </summary>
    public static class ScanCodes
    {
        public const string LocationPrefix = "LOC:";
        public const string BadgePrefix = "BADGE:";
        public const int MaxTagLength = 32;

        private static readonly Regex BarcodeTag = new Regex(@"^[A-Z0-9][A-Z0-9\-]{2,31}$", RegexOptions.CultureInvariant);
        private static readonly Regex RfidEpc = new Regex(@"^[0-9A-F]{24}$", RegexOptions.CultureInvariant);
        private static readonly Regex Code = new Regex(@"^[A-Z0-9][A-Z0-9\-]{0,19}$", RegexOptions.CultureInvariant);

        public static ScanCode Parse(string? raw)
        {
            var text = Clean(raw);
            if (text.Length == 0)
                return new ScanCode(ScanCodeKind.Empty, string.Empty);

            if (text.StartsWith(LocationPrefix, StringComparison.Ordinal))
                return Classified(ScanCodeKind.Location, text.Substring(LocationPrefix.Length), Code);

            if (text.StartsWith(BadgePrefix, StringComparison.Ordinal))
                return Classified(ScanCodeKind.Badge, text.Substring(BadgePrefix.Length), Code);

            if (RfidEpc.IsMatch(text) || BarcodeTag.IsMatch(text))
                return new ScanCode(ScanCodeKind.ItemTag, text);

            return new ScanCode(ScanCodeKind.Invalid, text);
        }

        public static bool IsRfidEpc(string tag) => RfidEpc.IsMatch(tag);

        /// <summary>Normalizes an item tag or throws <see cref="ArgumentException"/>.</summary>
        public static string NormalizeTag(string? raw)
        {
            var code = Parse(raw);
            if (code.Kind != ScanCodeKind.ItemTag)
                throw new ArgumentException($"'{raw}' is not a valid item tag.", nameof(raw));
            return code.Value;
        }

        private static ScanCode Classified(ScanCodeKind kind, string value, Regex pattern) =>
            pattern.IsMatch(value) ? new ScanCode(kind, value) : new ScanCode(ScanCodeKind.Invalid, value);

        private static string Clean(string? raw)
        {
            if (string.IsNullOrEmpty(raw))
                return string.Empty;

            var builder = new StringBuilder(raw!.Length);
            foreach (var c in raw)
            {
                if (!char.IsControl(c))
                    builder.Append(c);
            }
            return builder.ToString().Trim().ToUpperInvariant();
        }
    }
}
