namespace FoxMouse.Deployment;

public enum DeploymentOperation
{
    Install,
    Repair,
    Upgrade,
    Uninstall,
}

public enum InstallationKind
{
    Absent,
    Legacy,
    Managed,
    Invalid,
}

public sealed record InstallationStatus(
    InstallationKind Kind,
    string? Version,
    int StateSchemaVersion = 0);

public sealed record DeploymentOutcome(
    DeploymentOperation Operation,
    string Version,
    string InstallRoot);

public sealed class InstallRequest
{
    public required DeploymentPaths Paths { get; init; }

    public required string PackagePath { get; init; }

    public required string Version { get; init; }

    public bool LaunchAfterInstall { get; init; } = true;

    public string? InitialLanguage { get; init; }

    public string Publisher { get; init; } = "FoxMouse Project";

    public string? AboutUrl { get; init; }

    public IProductProcessController? ProcessController { get; init; }

    public IShortcutWriter? ShortcutWriter { get; init; }

    public Action<string>? Checkpoint { get; init; }
}

public sealed class UninstallRequest
{
    public required DeploymentPaths Paths { get; init; }

    public bool KeepSettings { get; init; } = true;

    public bool KeepMaintenanceHost { get; init; }

    public IProductProcessController? ProcessController { get; init; }

    public IShortcutWriter? ShortcutWriter { get; init; }

    public Action<string>? Checkpoint { get; init; }
}

public interface IProductProcessController
{
    bool IsApplicationRunning(string installRoot);

    void StopSafely(string installRoot, string recoveryExecutable);

    void StartApplication(string installRoot);
}

public interface IShortcutWriter
{
    void Create(string shortcutPath, string targetPath, string workingDirectory, string description);
}

public sealed class DeploymentException : Exception
{
    public DeploymentException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
