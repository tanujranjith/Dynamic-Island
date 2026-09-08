namespace DynamicIsland.Windows.Models;

public enum IntegrationState { Disabled, Connecting, Ready, PermissionRequired, Unavailable, Error }
public sealed record IntegrationStatus(IntegrationState State, string Message, string? SettingsUri = null)
{
    public bool CanRetry => State is IntegrationState.Error or IntegrationState.PermissionRequired or IntegrationState.Unavailable;
    public static IntegrationStatus Disabled { get; } = new(IntegrationState.Disabled, "Disabled");
}
