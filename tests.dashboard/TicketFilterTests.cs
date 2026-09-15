using TicketsDashboard;
using Xunit;

namespace TicketsDashboard.Tests;

public class TicketFilterTests
{
    private static readonly DateOnly Start = new(2025, 6, 1);

    [Fact]
    public void NormalRangeIsAccepted()
    {
        Assert.Null(new TicketFilter(Start, Start.AddDays(30), null, null).Validate());
    }

    [Fact]
    public void FullDatasetRangeIsAccepted()
    {
        // The synced table spans 2024-05-01 to 2025-07-26; the default view must not be rejected.
        var filter = new TicketFilter(new DateOnly(2024, 5, 1), new DateOnly(2025, 7, 27), null, null);

        Assert.Null(filter.Validate());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void EndMustFollowStart(int offset)
    {
        Assert.NotNull(new TicketFilter(Start, Start.AddDays(offset), null, null).Validate());
    }

    [Fact]
    public void ExcessiveRangesAreRejected()
    {
        var filter = new TicketFilter(Start, Start.AddDays(TicketFilter.MaxSpanDays + 1), null, null);

        Assert.NotNull(filter.Validate());
    }

    [Fact]
    public void ControlCharactersInFiltersAreRejected()
    {
        Assert.NotNull(new TicketFilter(Start, Start.AddDays(1), "EM\0EA", null).Validate());
    }

    [Fact]
    public void OverlongFilterValuesAreRejected()
    {
        Assert.NotNull(new TicketFilter(Start, Start.AddDays(1), new string('x', 65), null).Validate());
    }

    [Fact]
    public void RegionValuesFromTheDatasetAreAccepted()
    {
        foreach (var region in new[] { "EMEA", "APAC", "AMER" })
            Assert.Null(new TicketFilter(Start, Start.AddDays(1), region, null).Validate());
    }
}
