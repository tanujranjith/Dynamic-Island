using System;
using DynamicIsland.Windows.Infrastructure;
using Xunit;

namespace DynamicIsland.Windows.Tests;

// Covers the 10-second rewind/forward clamping for the media controls: normal seeks plus the
// near-start, near-end, exact-boundary and zero-duration edge cases.
public class SeekMathTests
{
    private static readonly TimeSpan Back = TimeSpan.FromSeconds(-10);
    private static readonly TimeSpan Forward = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Duration = TimeSpan.FromMinutes(3); // 180s

    [Fact]
    public void Forward_MidTrack_AdvancesTenSeconds()
    {
        var result = SeekMath.ClampSeek(TimeSpan.FromSeconds(57), Forward, Duration);
        Assert.Equal(TimeSpan.FromSeconds(67), result);
    }

    [Fact]
    public void Back_MidTrack_RewindsTenSeconds()
    {
        var result = SeekMath.ClampSeek(TimeSpan.FromSeconds(57), Back, Duration);
        Assert.Equal(TimeSpan.FromSeconds(47), result);
    }

    [Fact]
    public void Back_NearStart_ClampsToZero()
    {
        var result = SeekMath.ClampSeek(TimeSpan.FromSeconds(4), Back, Duration);
        Assert.Equal(TimeSpan.Zero, result);
    }

    [Fact]
    public void Back_AtStart_StaysAtZero()
    {
        var result = SeekMath.ClampSeek(TimeSpan.Zero, Back, Duration);
        Assert.Equal(TimeSpan.Zero, result);
    }

    [Fact]
    public void Forward_NearEnd_ClampsToDuration()
    {
        var result = SeekMath.ClampSeek(TimeSpan.FromSeconds(175), Forward, Duration);
        Assert.Equal(Duration, result);
    }

    [Fact]
    public void Forward_AtEnd_StaysAtDuration()
    {
        var result = SeekMath.ClampSeek(Duration, Forward, Duration);
        Assert.Equal(Duration, result);
    }

    [Fact]
    public void Forward_ExactlyTenFromEnd_LandsOnDuration()
    {
        var result = SeekMath.ClampSeek(TimeSpan.FromSeconds(170), Forward, Duration);
        Assert.Equal(Duration, result);
    }

    [Fact]
    public void UnknownDuration_DoesNotClampUpper()
    {
        // Duration == 0 means "unknown" (live stream); the upper clamp must not pin everything to 0.
        var result = SeekMath.ClampSeek(TimeSpan.FromSeconds(57), Forward, TimeSpan.Zero);
        Assert.Equal(TimeSpan.FromSeconds(67), result);
    }
}
