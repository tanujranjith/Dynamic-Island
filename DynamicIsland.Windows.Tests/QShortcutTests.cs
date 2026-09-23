using System.Text.Json;
using DynamicIsland.Windows.Infrastructure;
using DynamicIsland.Windows.Models;
using Xunit;

namespace DynamicIsland.Windows.Tests;

public class QShortcutTests
{
    [Fact]
    public void LowercaseSequenceRearmsAfterEveryMatch()
    {
        var matcher = new QSequenceMatcher();
        for (var i = 0; i < 10; i++)
        {
            Assert.False(matcher.Press('V', i * 3000, 1, false, false));
            Assert.False(matcher.Press('A', i * 3000 + 100, 1, false, false));
            Assert.True(matcher.Press('R', i * 3000 + 200, 1, false, false));
        }
    }

    [Fact]
    public void LowercaseSequenceMustCompleteWithinTwoSeconds()
    {
        var matcher = new QSequenceMatcher();
        Assert.False(matcher.Press('V', 0, 1, false, false));
        Assert.False(matcher.Press('A', 1000, 1, false, false));
        Assert.True(matcher.Press('R', 2000, 1, false, false));
        Assert.False(matcher.Press('R', 2001, 1, false, false));
        matcher.Press('V', 3000, 1, false, false);
        matcher.Press('A', 4000, 1, false, false);
        Assert.False(matcher.Press('R', 5001, 1, false, false));
    }

    [Theory]
    [InlineData('X', 1, false)]
    [InlineData('A', 2, false)]
    [InlineData('A', 1, true)]
    [InlineData(8, 1, false)]
    public void OtherKeysWindowChangesOrUppercaseBreakSequence(uint middle, int window, bool uppercaseOrModified)
    {
        var matcher = new QSequenceMatcher();
        matcher.Press('V', 0, 1, false, false);
        matcher.Press(middle, 100, window, uppercaseOrModified, false);
        Assert.False(matcher.Press('R', 200, 1, false, false));
    }

    [Fact]
    public void RepeatKeysDoNotCompleteOrRestartSequence()
    {
        var matcher = new QSequenceMatcher();
        matcher.Press('V', 0, 1, false, false);
        matcher.Press('V', 1000, 1, false, true);
        matcher.Press('A', 1500, 1, false, false);
        Assert.False(matcher.Press('R', 2500, 1, false, false));
    }

    [Fact]
    public void ResetDiscardsPartialSequence()
    {
        var matcher = new QSequenceMatcher();
        matcher.Press('V', 0, 1, false, false);
        matcher.Press('A', 100, 1, false, false);
        matcher.Reset();
        Assert.False(matcher.Press('R', 200, 1, false, false));
    }

    [Fact]
    public void MigrationPreservesLegacyAndAllowsAllOrNone()
    {
        Assert.Equal(QActivationShortcuts.CtrlAltQ, QActivationPolicy.Resolve(null, null));
        Assert.Equal(QActivationShortcuts.ShiftA, QActivationPolicy.Resolve(null, "Shift+A"));
        Assert.Equal(QActivationShortcuts.None, QActivationPolicy.Resolve(QActivationShortcuts.None, "Shift+A"));
        var choices = QActivationShortcuts.CtrlAltQ | QActivationShortcuts.VarSequence | QActivationShortcuts.ShiftPeriod;
        var saved = JsonSerializer.Deserialize<QActivationShortcuts>(JsonSerializer.Serialize(choices));
        Assert.Equal(choices, QActivationPolicy.Resolve(saved, "Shift+A"));
        Assert.Equal(QActivationShortcuts.All, QActivationPolicy.Resolve((QActivationShortcuts)255, null));
    }
}
