using Themearr.API.Services;

namespace Themearr.API.Tests;

public class LogSanitizerTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("movie-123", "movie-123")]
    [InlineData("srv1:42", "srv1:42")]
    public void Clean_leavesSafeValuesUnchanged(string? input, string expected)
    {
        Assert.Equal(expected, LogSanitizer.Clean(input));
    }

    [Theory]
    // CR/LF are the log-forging vector — they must be stripped so a value can't
    // start a new (attacker-controlled) log line.
    [InlineData("evil\r\nINJECTED admin login", "evilINJECTED admin login")]
    [InlineData("a\nb", "ab")]
    [InlineData("a\rb", "ab")]
    [InlineData("\r\n\r\n", "")]
    public void Clean_stripsNewlines(string input, string expected)
    {
        Assert.Equal(expected, LogSanitizer.Clean(input));
    }
}
