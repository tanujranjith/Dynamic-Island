using DynamicIsland.Windows.Infrastructure;
using Xunit;

public class ThemePaletteTests
{
    [Theory]
    [InlineData("#abc", "#AABBCC")]
    [InlineData("  #12ab34  ", "#12AB34")]
    [InlineData("FFFFFF", "#FFFFFF")]
    public void NormalizesHex(string input, string expected)
    {
        Assert.True(ThemeColor.TryParse(input, out var color)); Assert.Equal(expected, color.Hex);
    }
    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("#")] [InlineData("#12345")]
    [InlineData("#ZZZZZZ")] [InlineData("#12345678")]
    public void RejectsInvalidHex(string? input) => Assert.False(ThemeColor.TryParse(input, out _));
    [Fact]
    public void PalettePreservesChosenBackground() => Assert.Equal("#123D38", ThemePalette.Custom("#123D38").Surface);
    [Fact]
    public void BrightAndDarkColorsChooseReadableText()
    {
        Assert.Equal("#000000", ThemePalette.Custom("#FFFFFF").Text);
        Assert.Equal("#FFFFFF", ThemePalette.Custom("#000000").Text);
    }
    [Fact]
    public void ContrastRemainsReadableAcrossColorCube()
    {
        for (var r = 0; r <= 255; r += 17)
        for (var g = 0; g <= 255; g += 17)
        for (var b = 0; b <= 255; b += 17)
        {
            var palette = ThemePalette.Custom(new ThemeColor((byte)r, (byte)g, (byte)b).Hex);
            foreach (var background in new[] { palette.Surface, palette.Card, palette.Control, palette.Hover })
                Assert.True(ThemeColor.Parse(palette.Text).Contrast(ThemeColor.Parse(background)) >= 4.5, palette.Surface);
            Assert.True(ThemeColor.Parse(palette.Muted).Contrast(ThemeColor.Parse(palette.Surface)) >= 4.5);
            Assert.True(ThemeColor.Parse(palette.Accent).Contrast(ThemeColor.Parse(palette.Surface)) >= 4.5);
        }
    }
}
