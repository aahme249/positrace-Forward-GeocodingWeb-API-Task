using GeocodingApi.Services;
using Xunit;

namespace GeocodingApi.Tests;

public class AddressNormalizerTests
{
    private readonly AddressNormalizer _sut = new();

    [Theory]
    [InlineData("123-12 Main St, Toronto, ON", "123 Main St, Toronto, ON")]
    [InlineData("456-7 Bathurst St, Toronto, ON M5T 2S5", "456 Bathurst St, Toronto, ON M5T 2S5")]
    public void DashUnit_StripsLeadingCivicDashPrefix(string input, string expected)
        => Assert.Equal(expected, _sut.Normalize(input));

    [Theory]
    [InlineData("Apt 3B 250 King St E, Toronto, ON M5A 1J7", "250 King St E, Toronto, ON M5A 1J7")]
    [InlineData("Apt. 7 100 Queen St W, Toronto, ON M5H 2N2", "100 Queen St W, Toronto, ON M5H 2N2")]
    public void Apt_StripsAptQualifier(string input, string expected)
        => Assert.Equal(expected, _sut.Normalize(input));

    [Theory]
    [InlineData("Unit 12A 8028 120 St, Surrey, BC V3W 3N3", "8028 120 St, Surrey, BC V3W 3N3")]
    [InlineData("Unit 42 100 Main St, Ottawa, ON K1P 1J1", "100 Main St, Ottawa, ON K1P 1J1")]
    public void Unit_StripsStandardIdentifier(string input, string expected)
        => Assert.Equal(expected, _sut.Normalize(input));

    [Fact]
    public void Unit_StripsSpacedLetterSuffix()
        => Assert.Equal("8028 120 St, Surrey, BC V3W 3N3",
            _sut.Normalize("Unit 12 A 8028 120 St, Surrey, BC V3W 3N3"));

    [Theory]
    [InlineData("Suite 200 1090 West Georgia St, Vancouver, BC V6E 3V7", "1090 West Georgia St, Vancouver, BC V6E 3V7")]
    [InlineData("Ste. 5 123 Front St, Toronto, ON M5J 1E6", "123 Front St, Toronto, ON M5J 1E6")]
    [InlineData("Ste 3 456 Bay St, Toronto, ON M5H 2S6", "456 Bay St, Toronto, ON M5H 2S6")]
    public void Suite_StripsQualifierAndSteAlias(string input, string expected)
        => Assert.Equal(expected, _sut.Normalize(input));

    [Fact]
    public void Room_StripsRoomQualifier()
        => Assert.Equal("100 Front St W, Toronto, ON M5J 1E3",
            _sut.Normalize("Room 412 100 Front St W, Toronto, ON M5J 1E3"));

    [Fact]
    public void Hash_StripsHashQualifier()
        => Assert.Equal("2568 Granville St, Vancouver, BC V6H 3G8",
            _sut.Normalize("#5 2568 Granville St, Vancouver, BC V6H 3G8"));

    [Theory]
    [InlineData("App. 4 3500 Rue Sherbrooke O, Montreal, QC H3Z 1E5", "3500 Rue Sherbrooke Ouest, Montreal, QC H3Z 1E5")]
    [InlineData("App 12 500 Rue Saint-Denis, Montreal, QC H2J 2L5", "500 Rue Saint-Denis, Montreal, QC H2J 2L5")]
    public void French_App_StripsAppartementQualifier(string input, string expected)
        => Assert.Equal(expected, _sut.Normalize(input));

    [Theory]
    [InlineData("No. 8 1801 Rue McGill College, Montreal, QC H3A 2N4", "1801 Rue McGill College, Montreal, QC H3A 2N4")]
    [InlineData("No 3 420 Rue De La Montagne, Montreal, QC H3G 1Z8", "420 Rue De La Montagne, Montreal, QC H3G 1Z8")]
    public void French_No_StripsNumeroQualifier(string input, string expected)
        => Assert.Equal(expected, _sut.Normalize(input));

    [Fact]
    public void French_Bureau_StripsBureauQualifier()
        => Assert.Equal("1000 Rue De La Gauchetière Ouest, Montreal, QC H3B 4W5",
            _sut.Normalize("Bureau 7 1000 Rue De La Gauchetière O, Montreal, QC H3B 4W5"));

    [Fact]
    public void NoQualifier_ReturnsTrimmedInput()
        => Assert.Equal("100 Queen St W, Toronto, ON M5H 2N2",
            _sut.Normalize("  100 Queen St W, Toronto, ON M5H 2N2  "));

    [Theory]
    [InlineData("Toronto, ON M5V 3A8", "M5V3A8")]
    [InlineData("Vancouver, BC V6G 1C7", "V6G1C7")]
    [InlineData("99999 Nowhere Blvd, Toronto, ON M5V3A8", "M5V3A8")]
    public void ExtractPostalCode_ParsesCanadianPostalCode(string input, string expected)
        => Assert.Equal(expected, _sut.ExtractPostalCode(input));

    [Fact]
    public void ExtractPostalCode_ReturnsNull_WhenNoPostalCode()
        => Assert.Null(_sut.ExtractPostalCode("Unit 5 99999 Nowhere Blvd, Faketown, ON"));
}
