namespace Gelato.Tests.Common;

public class UtilsParseToTicksTests
{
    [Theory]
    [InlineData("2:29:00", 89400000000L)]
    [InlineData("02:29:00", 89400000000L)]
    [InlineData("2h29min", 89400000000L)]
    [InlineData("2h 29min", 89400000000L)]
    [InlineData("1h", 36000000000L)]
    [InlineData("90s", 900000000L)]
    [InlineData("45sec", 450000000L)]
    [InlineData("PT90S", 900000000L)]
    [InlineData("1.02:03:04", 937840000000L)]
    public void ParsesSupportedFormats(string input, long expectedTicks)
    {
        Assert.Equal(expectedTicks, Utils.ParseToTicks(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankInput_ReturnsNull(string? input)
    {
        Assert.Null(Utils.ParseToTicks(input));
    }

    [Fact]
    public void Unparseable_ReturnsZeroNotNull()
    {
        // Every strategy fails, and the final regex fallback yields TimeSpan.Zero.
        Assert.Equal(0L, Utils.ParseToTicks("abc"));
    }

    /// <summary>
    /// KNOWN DEFECT, pinned deliberately. The code intends a bare number to mean minutes
    /// (see the `onlyNum` branch), but TimeSpan.TryParse accepts a bare integer as DAYS and
    /// returns first, so that branch is unreachable. "149" therefore parses as 149 days.
    /// Fixing this is a behaviour change; when it is fixed, update this expectation to
    /// 149 minutes (89400000000) in the same commit.
    /// </summary>
    [Fact]
    public void BareNumber_IsCurrentlyParsedAsDays_KnownDefect()
    {
        Assert.Equal(TimeSpan.FromDays(149).Ticks, Utils.ParseToTicks("149"));
    }

    /// <summary>
    /// KNOWN DEFECT, pinned deliberately. Input is lower-cased before XmlConvert.ToTimeSpan,
    /// which is case-sensitive and rejects "pt2h29m". The regex fallback then matches "2h"
    /// but not "29m" (it requires "min"), so the minutes are lost. "PT90S" survives only
    /// because the seconds regex accepts a bare "s".
    /// </summary>
    [Fact]
    public void Iso8601WithMinutes_LosesMinutes_KnownDefect()
    {
        Assert.Equal(TimeSpan.FromHours(2).Ticks, Utils.ParseToTicks("PT2H29M"));
    }
}
