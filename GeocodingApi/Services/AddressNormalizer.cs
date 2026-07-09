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

    // Dash-prefixed unit: Canada Post's addressing convention is "unit number - civic number"
    // (e.g. "4-123 Main St" = unit 4, civic number 123), so the leading number is the unit to
    // discard and the second is the real street number to keep: "123-12 Main St" → "12 Main St".
    // Must run first — anchors on the leading digits before other passes alter them.
    [GeneratedRegex(@"^\d+-(\d+)\b")]
    private static partial Regex DashUnitRegex();

    // Unit identifier pattern used across English and French qualifiers.
    // Matches a primary token (alphanumeric/dash/#) plus an optional single trailing
    // letter separated by a space (e.g. "12 A"), covering identifiers like "12A", "12 A", "B3", "#7".
    // The optional trailing letter is anchored with \b so it doesn't consume the next word.
    private const string UnitToken = @"[\w#-]+(?:\s+[A-Za-z]\b)?";

    // English: Apt / Apt.
    [GeneratedRegex(@"\bApt\.?\s+[\w#-]+(?:\s+[A-Za-z]\b)?,?", RegexOptions.IgnoreCase)]
    private static partial Regex AptRegex();

    // English: Unit
    [GeneratedRegex(@"\bUnit\s+[\w#-]+(?:\s+[A-Za-z]\b)?,?", RegexOptions.IgnoreCase)]
    private static partial Regex UnitRegex();

    // English: Suite / Ste / Ste.
    [GeneratedRegex(@"\b(?:Suite|Ste)\.?\s+[\w#-]+(?:\s+[A-Za-z]\b)?,?", RegexOptions.IgnoreCase)]
    private static partial Regex SuiteRegex();

    // English: Room (hotel/casino room numbers)
    [GeneratedRegex(@"\bRoom\s+[\w#-]+(?:\s+[A-Za-z]\b)?,?", RegexOptions.IgnoreCase)]
    private static partial Regex RoomRegex();

    // English: Building / Bldg
    [GeneratedRegex(@"\b(?:Building|Bldg)\.?\s+[\w#-]+(?:\s+[A-Za-z]\b)?,?", RegexOptions.IgnoreCase)]
    private static partial Regex BuildingRegex();

    // English: Floor / Fl (e.g. "Floor 3", "Fl. 2nd")
    [GeneratedRegex(@"\b(?:Floor|Fl)\.?\s+\d+(?:st|nd|rd|th)?,?", RegexOptions.IgnoreCase)]
    private static partial Regex FloorRegex();

    // French: App / App. (Appartement)
    [GeneratedRegex(@"\bApp\.?\s+[\w#-]+(?:\s+[A-Za-z]\b)?,?", RegexOptions.IgnoreCase)]
    private static partial Regex AppRegex();

    // French: No / No. (Numéro — used like "No 3" or "No. 4B")
    // Anchored with word boundaries and a required space+token so bare "No" in street names is safe.
    [GeneratedRegex(@"\bNo\.?\s+[\w#-]+(?:\s+[A-Za-z]\b)?,?", RegexOptions.IgnoreCase)]
    private static partial Regex NoRegex();

    // French: Bureau (office unit)
    [GeneratedRegex(@"\bBureau\s+[\w#-]+(?:\s+[A-Za-z]\b)?,?", RegexOptions.IgnoreCase)]
    private static partial Regex BureauRegex();

    // "#" followed by identifier (e.g. "#3", "# 3A")
    [GeneratedRegex(@"#\s*[\w-]+,?")]
    private static partial Regex HashUnitRegex();

    // French directional suffix — "O" (Ouest) is the only single-letter directional with no English
    // equivalent, so it's safe to expand unconditionally. E/N/S are ambiguous (English East/North/South
    // use the same letters) and are left as-is to avoid breaking Ontario/BC addresses.
    [GeneratedRegex(@"(?<=\s)O(?=\s*(?:,|$))")]
    private static partial Regex OuestRegex();

    // Collapse multiple spaces and orphaned commas left after qualifier removal
    [GeneratedRegex(@"[ \t]{2,}")]
    private static partial Regex MultiSpaceRegex();

    [GeneratedRegex(@",\s*,")]
    private static partial Regex DoubleCommaRegex();

    public string? ExtractPostalCode(string address)
    {
        var match = PostalCodeRegex().Match(address);
        if (!match.Success) return null;
        return (match.Groups[1].Value + match.Groups[2].Value).ToUpperInvariant();
    }

    public string Normalize(string address)
    {
        var result = address.Trim();

        // 1. Strip dash-prefixed unit at the very start before any other pass can shift the leading digits
        result = DashUnitRegex().Replace(result, "$1");

        // 2. English unit qualifiers
        result = AptRegex().Replace(result, " ");
        result = UnitRegex().Replace(result, " ");
        result = SuiteRegex().Replace(result, " ");
        result = RoomRegex().Replace(result, " ");
        result = BuildingRegex().Replace(result, " ");
        result = FloorRegex().Replace(result, " ");
        result = HashUnitRegex().Replace(result, " ");

        // 3. French unit qualifiers
        result = AppRegex().Replace(result, " ");
        result = NoRegex().Replace(result, " ");
        result = BureauRegex().Replace(result, " ");

        // 4. Expand French directional "O" → "Ouest" so Nominatim matches OSM street names
        //    e.g. "Rue Sherbrooke O," → "Rue Sherbrooke Ouest,"
        result = OuestRegex().Replace(result, "Ouest");

        // 5. Tidy up whitespace and orphaned punctuation left by the removals above
        result = DoubleCommaRegex().Replace(result, ",");
        result = MultiSpaceRegex().Replace(result, " ");
        result = result.Trim().Trim(',').Trim();

        return result;
    }
}
