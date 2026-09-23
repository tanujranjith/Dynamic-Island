namespace DynamicIsland.Windows.Models;

public sealed record QProviderPreference(string Model, string ReasoningEffort);

public sealed record QProviderChoice(string Id, string Name)
{
    public override string ToString() => Name;
}

public static class QProviderSelection
{
    // Only non-secret preferences belong in settings.json. Credentials stay in IQSecretStore.
    public static QProviderPreference Switch(IDictionary<string, QProviderPreference> preferences,
        string previousProvider, string previousModel, string previousEffort, string provider, string defaultModel)
    {
        preferences[previousProvider.ToLowerInvariant()] = new(previousModel, previousEffort);
        return preferences.TryGetValue(provider.ToLowerInvariant(), out var saved)
            && saved is not null && !string.IsNullOrWhiteSpace(saved.Model)
            ? saved : new(defaultModel, "auto");
    }
}
