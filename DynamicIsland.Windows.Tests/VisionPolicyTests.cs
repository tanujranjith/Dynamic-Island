using DynamicIsland.Windows.Models;
using Xunit;

namespace DynamicIsland.Windows.Tests;

public class VisionPolicyTests
{
    private static IReadOnlySet<int> Owners(params int[] indexes) => new HashSet<int>(indexes);

    [Fact]
    public void Privacy_NotEnrolled_Enrolling_PromptsRegistering()
    {
        var d = VisionPolicy.Decide(detectorReady: true, privacyOn: true, enrolled: false,
            enrolling: true, peopleCount: 1, Owners());
        Assert.Equal(VisionLevel.Gray, d.Level);
        Assert.Equal("Registering your face...", d.Status);
        Assert.False(d.Alert);
    }

    [Fact]
    public void Privacy_NotEnrolled_Idle_PromptsRegister()
    {
        var d = VisionPolicy.Decide(true, true, false, false, 0, Owners());
        Assert.Equal(VisionLevel.Gray, d.Level);
        Assert.Equal("Register your face", d.Status);
        Assert.False(d.Alert);
    }

    [Fact]
    public void NotReady_WithPeople_FallbackWarning()
    {
        var d = VisionPolicy.Decide(false, false, false, false, 2, Owners());
        Assert.Equal(VisionLevel.Red, d.Level);
        Assert.Equal("2 person/people detected (fallback)", d.Status);
        Assert.True(d.Alert);
    }

    [Fact]
    public void NotReady_Empty_PromptsInstallModel()
    {
        var d = VisionPolicy.Decide(false, false, false, false, 0, Owners());
        Assert.Equal(VisionLevel.Gray, d.Level);
        Assert.Equal("Install person model for reliable monitoring", d.Status);
        Assert.False(d.Alert);
    }

    [Fact]
    public void PrivacyOff_NoPerson_Green()
    {
        var d = VisionPolicy.Decide(true, false, false, false, 0, Owners());
        Assert.Equal(VisionLevel.Green, d.Level);
        Assert.Equal("No person detected", d.Status);
        Assert.False(d.Alert);
    }

    [Fact]
    public void PrivacyOff_OnePerson_Red()
    {
        var d = VisionPolicy.Decide(true, false, false, false, 1, Owners());
        Assert.Equal(VisionLevel.Red, d.Level);
        Assert.Equal("Person detected", d.Status);
        Assert.True(d.Alert);
    }

    [Fact]
    public void PrivacyOff_ManyPeople_Red()
    {
        var d = VisionPolicy.Decide(true, false, false, false, 3, Owners());
        Assert.Equal(VisionLevel.Red, d.Level);
        Assert.Equal("3 people detected", d.Status);
        Assert.True(d.Alert);
    }

    [Fact]
    public void Privacy_Enrolled_Empty_AllClear()
    {
        var d = VisionPolicy.Decide(true, true, true, false, 0, Owners());
        Assert.Equal(VisionLevel.Green, d.Level);
        Assert.Equal("All clear", d.Status);
        Assert.False(d.Alert);
    }

    [Fact]
    public void Privacy_Enrolled_JustOwner_JustYou()
    {
        var d = VisionPolicy.Decide(true, true, true, false, 1, Owners(0));
        Assert.Equal(VisionLevel.Green, d.Level);
        Assert.Equal("Just you", d.Status);
        Assert.False(d.Alert);
    }

    [Fact]
    public void Privacy_Enrolled_MultiplePeople_Red()
    {
        var d = VisionPolicy.Decide(true, true, true, false, 2, Owners(0));
        Assert.Equal(VisionLevel.Red, d.Level);
        Assert.Equal("2 people detected", d.Status);
        Assert.True(d.Alert);
    }

    [Fact]
    public void Privacy_Enrolled_OneStranger_Unknown()
    {
        var d = VisionPolicy.Decide(true, true, true, false, 1, Owners());
        Assert.Equal(VisionLevel.Red, d.Level);
        Assert.Equal("Unknown person detected", d.Status);
        Assert.True(d.Alert);
    }

    [Theory]
    [InlineData(VisionLevel.Red, "#FF3B30")]
    [InlineData(VisionLevel.Green, "#30D158")]
    [InlineData(VisionLevel.Gray, "#636366")]
    public void ColorHex_MapsLevels(VisionLevel level, string hex) =>
        Assert.Equal(hex, VisionPolicy.ColorHex(level));

    [Fact]
    public void VisionState_NotRunning_IsNeutralGray()
    {
        var state = new VisionState { Availability = VisionAvailability.Initializing };
        Assert.Equal(VisionLevel.Gray, state.Level);
        Assert.False(state.Alert);
    }

    [Fact]
    public void VisionState_Running_UsesPolicyAndSignature()
    {
        var state = new VisionState
        {
            Availability = VisionAvailability.Running,
            DetectorReady = true, PrivacyOn = true, Enrolled = true,
            PeopleCount = 1, OwnerSignature = VisionState.BuildSignature(new[] { 0 })
        };
        Assert.Equal("Just you", state.StatusText);
        Assert.Equal(VisionLevel.Green, state.Level);
    }
}
