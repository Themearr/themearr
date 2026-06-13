using Themearr.API.Services;

namespace Themearr.API.Tests;

public class PollBackoffTests
{
    [Theory]
    [InlineData(1, 1)]   // first processing poll waits 1s
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(4, 8)]
    [InlineData(5, 15)]  // 16s capped to 15s
    [InlineData(6, 15)]  // stays capped
    [InlineData(50, 15)] // never grows unbounded
    public void ForAttempt_growsExponentially_thenCapsAt15s(int attempt, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), PollBackoff.ForAttempt(attempt));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void ForAttempt_nonPositiveAttempt_treatedAsFirst(int attempt)
    {
        // A defensive caller passing 0/negative shouldn't get a zero or negative delay.
        Assert.Equal(TimeSpan.FromSeconds(1), PollBackoff.ForAttempt(attempt));
    }
}
