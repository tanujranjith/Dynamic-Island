namespace DynamicIsland.Windows.Infrastructure;

public static class AppDataPaths
{
    public static string Root => IsPreview && Environment.GetEnvironmentVariable("ISLAND_PREVIEW_DATA") is string preview && Path.IsPathFullyQualified(preview) ? preview : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DynamicIsland.Windows" + (IsPreview ? ".CoreUpgradesPreview" : ""));
    public static bool IsPreview => Environment.GetEnvironmentVariable("ISLAND_PREVIEW") == "1";
    public static string InstanceSuffix => IsPreview ? ".CoreUpgradesPreview" : "";
}
