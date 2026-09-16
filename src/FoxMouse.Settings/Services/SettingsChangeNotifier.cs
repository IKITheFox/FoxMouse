using FoxMouse.Platform.Windows.Configuration;

namespace FoxMouse.Settings.Services;

public interface ISettingsChangeNotifier
{
    Task<bool> NotifyReloadAsync(string settingsPath, CancellationToken cancellationToken = default);

    Task<bool> NotifyPreviewAsync(string settingsPath, CancellationToken cancellationToken = default);
}

public sealed class SettingsChangeNotifier : ISettingsChangeNotifier
{
    public Task<bool> NotifyReloadAsync(
        string settingsPath,
        CancellationToken cancellationToken = default) =>
        SettingsChangeChannel.NotifyReloadAsync(settingsPath, cancellationToken);

    public Task<bool> NotifyPreviewAsync(
        string settingsPath,
        CancellationToken cancellationToken = default) =>
        SettingsChangeChannel.NotifyPreviewAsync(settingsPath, cancellationToken);
}

/// <summary>Isolated UI verification never notifies the user's running application.</summary>
internal sealed class IsolatedSettingsChangeNotifier : ISettingsChangeNotifier
{
    public Task<bool> NotifyReloadAsync(string settingsPath, CancellationToken cancellationToken = default) => Task.FromResult(false);
    public Task<bool> NotifyPreviewAsync(string settingsPath, CancellationToken cancellationToken = default) => Task.FromResult(false);
}
