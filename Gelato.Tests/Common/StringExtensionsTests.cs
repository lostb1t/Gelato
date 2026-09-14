namespace Gelato.Tests.Common;

public class StringExtensionsTests
{
    [Theory]
    [InlineData("http://example.com", true)]
    [InlineData("https://example.com/path", true)]
    [InlineData("HTTPS://EXAMPLE.COM", true)]
    [InlineData("ftp://example.com", false)]
    [InlineData("example.com", false)]
    [InlineData("", false)]
    public void IsUrl_AcceptsOnlyHttpSchemes(string input, bool expected)
    {
        Assert.Equal(expected, input.IsUrl());
    }
}
