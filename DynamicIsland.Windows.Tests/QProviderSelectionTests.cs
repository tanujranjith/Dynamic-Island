using System.Text.Json;
using DynamicIsland.Windows.Models;
using Xunit;

namespace DynamicIsland.Windows.Tests;

public sealed class QProviderSelectionTests
{
    [Fact]
    public void NewProviderUsesItsDefaultNotThePreviousProvidersModel()
    {
        var saved = new Dictionary<string, QProviderPreference>();
        var selected = QProviderSelection.Switch(saved, "openai", "custom-openai", "high", "gemini", "gemini-default");
        Assert.Equal(new("gemini-default", "auto"), selected);
        Assert.Equal(new("custom-openai", "high"), saved["openai"]);
    }

    [Fact]
    public void RoundTripRestoresEachProvidersCustomModelAndEffort()
    {
        var saved = new Dictionary<string, QProviderPreference>();
        QProviderSelection.Switch(saved, "OpenAI", "custom-openai", "high", "gemini", "gemini-default");
        var selected = QProviderSelection.Switch(saved, "gemini", "custom-gemini", "low", "OPENAI", "openai-default");
        Assert.Equal(new("custom-openai", "high"), selected);
        var again = QProviderSelection.Switch(saved, "openai", selected.Model, selected.ReasoningEffort, "gemini", "gemini-default");
        Assert.Equal(new("custom-gemini", "low"), again);
    }

    [Fact]
    public void PreferencesSurviveSerialization()
    {
        var saved = new Dictionary<string, QProviderPreference> { ["anthropic"] = new("custom-claude", "high") };
        var restored = JsonSerializer.Deserialize<Dictionary<string, QProviderPreference>>(JsonSerializer.Serialize(saved))!;
        Assert.Equal(saved["anthropic"], QProviderSelection.Switch(restored, "gemini", "gemini-default", "auto", "anthropic", "claude-default"));
    }

    [Fact]
    public void InvalidSavedModelFallsBackToProviderDefault()
    {
        var saved = new Dictionary<string, QProviderPreference> { ["anthropic"] = new(" ", "high") };
        Assert.Equal(new("claude-default", "auto"), QProviderSelection.Switch(saved, "openai", "model", "high", "anthropic", "claude-default"));
    }
}
