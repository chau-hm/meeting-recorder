using MeetingEvidenceRecorder.Core.Evidence;

namespace MeetingEvidenceRecorder.Core.Tests;

public class ContractTests
{
    [Theory]
    [InlineData(0L)] [InlineData(1L)] [InlineData(999L)] [InlineData(1000L)]
    [InlineData(60000L)] [InlineData(3600000L)] [InlineData(7200000L)] [InlineData(long.MaxValue)]
    public void CanonicalPlaybackMillisecondsAreIntegerAndIndependentOfIdentity(long timestamp)
    {
        var shot = new ScreenshotEvent { EventId = "evt-first", TimestampMs = timestamp, Asset = "screenshots/arbitrary.png" };
        Assert.Empty(ContractValidation.Validate(shot));
        Assert.Empty(ContractValidation.Validate(shot with { EventId = "evt-second" }));
    }

    [Theory]
    [InlineData(-1L, true)] [InlineData(1000L, false)] [InlineData(2000L, false)] [InlineData(2001L, true)]
    public void DurationBoundaryIncludesExactlyOneSecondTolerance(long timestamp, bool error)
    {
        var shot = new ScreenshotEvent { EventId = "evt", TimestampMs = timestamp, Asset = "a.png" };
        Assert.Equal(error, ContractValidation.Validate(shot, 1000).Any(d => d.Severity == DiagnosticSeverity.Error));
    }

    [Theory]
    [InlineData("1.0", true)] [InlineData("1.99", true)] [InlineData("2.0", false)]
    [InlineData("1", false)] [InlineData("1.0.0", false)] [InlineData("v1.0", false)]
    public void SchemaMajorIsExplicit(string version, bool supported) => Assert.Equal(supported, ContractValidation.SupportsSchema(version));
}
