using System.Text.RegularExpressions;

namespace GeocodingApi.Services;

public interface IAddressNormalizer
{
    string Normalize(string address);
    string? ExtractPostalCode(string address);
}

public partial class AddressNormalizer : IAddressNormalizer
{
    // Canadian postal code: letter-digit-letter (space?) digit-letter-digit
    [GeneratedRegex(@"\b([A-Za-z]\d[A-Za-z])\s?(\d[A-Za-z]\d)\b")]
    private static partial Regex PostalCodeRegex();

    // "Apt." or "Apt" (with or without period) followed by unit identifier
    [GeneratedRegex(@"\bApt\.?\s+[\w#-]+,?", RegexOptions.IgnoreCase)]
    private static partial Regex AptRegex();

    // "Unit" followed by unit identifier
    [GeneratedRegex(@"\bUnit\s+[\w#-]+,?", RegexOptions.IgnoreCase)]
    private static partial Regex UnitRegex();

    // "Suite" followed by unit identifier
    [GeneratedRegex(@"\bSuite\s+[\w#-]+,?", RegexOptions.IgnoreCase)]
    private static partial Regex SuiteRegex();

    // "#" followed by identifier (e.g. "#3", "# 3A")
    [GeneratedRegex(@"#\s*[\w-]+,?")]
    private static partial Regex HashUnitRegex();

    // Dash-prefixed unit: leading civic-number dash unit-number (e.g. "123-12 Main St" → "123 Main St")
    // Matches at the start of the trimmed string: digits-digits followed by a word boundary
    [GeneratedRegex(@"^(\d+)-\d+\b")]
    private static partial Regex DashUnitRegex();

    // Collapse multiple spaces/commas
    [GeneratedRegex(@"[ \t]{2,}")]
    private static partial Regex MultiSpaceRegex();

    [GeneratedRegex(@",\s*,")]
    private static partial Regex DoubleCommaRegex();

    public string? ExtractPostalCode(string address)
    {
        var match = PostalCodeRegex().Match(address);
        if (!match.Success) return null;
        // Return without space, uppercase
        return (match.Groups[1].Value + match.Groups[2].Value).ToUpperInvariant();
    }

    public string Normalize(string address)
    {
        var result = address.Trim();

        // 1. Strip dash-prefixed unit at the start FIRST (before other passes alter the leading digits)
        result = DashUnitRegex().Replace(result, "$1");

        // 2. Strip named qualifiers and their unit identifiers
        result = AptRegex().Replace(result, " ");
        result = UnitRegex().Replace(result, " ");
        result = SuiteRegex().Replace(result, " ");
        result = HashUnitRegex().Replace(result, " ");

        // 3. Tidy up whitespace and orphaned commas/punctuation
        result = DoubleCommaRegex().Replace(result, ",");
        result = MultiSpaceRegex().Replace(result, " ");
        result = result.Trim().Trim(',').Trim();

        return result;
    }
}
