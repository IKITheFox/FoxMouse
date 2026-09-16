namespace FoxMouse.Deployment;

/// <summary>
/// Describes the operation, if any, that was attempted by a maintenance window.
/// </summary>
public sealed record MaintenanceSessionResult(
    bool Attempted,
    DeploymentOperation? Operation,
    bool Succeeded,
    int ExitCode,
    Exception? Error)
{
    internal static MaintenanceSessionResult NotAttempted { get; } =
        new(false, null, false, 0, null);
}

internal enum MaintenanceUiState
{
    Idle,
    ConfirmingUninstall,
    Running,
    Failed,
    SucceededAwaitingConfirmation,
    SucceededClosing,
}

internal enum MaintenanceCloseDisposition
{
    CloseNow,
    Deferred,
}

/// <summary>
/// Keeps window lifecycle decisions independent from WinForms so they can be
/// exercised without running an installer transaction.
/// </summary>
internal sealed class MaintenanceSessionController
{
    public MaintenanceUiState State { get; private set; } = MaintenanceUiState.Idle;

    public bool CloseRequested { get; private set; }

    public MaintenanceSessionResult Result { get; private set; } =
        MaintenanceSessionResult.NotAttempted;

    public void BeginConfirmation()
    {
        if (State is MaintenanceUiState.Idle or MaintenanceUiState.Failed)
        {
            State = MaintenanceUiState.ConfirmingUninstall;
        }
    }

    public void CancelConfirmation()
    {
        if (State == MaintenanceUiState.ConfirmingUninstall)
        {
            State = MaintenanceUiState.Idle;
        }
    }

    public bool BeginOperation(DeploymentOperation? operation)
    {
        if (State is MaintenanceUiState.Running or MaintenanceUiState.SucceededClosing or MaintenanceUiState.SucceededAwaitingConfirmation)
        {
            return false;
        }

        State = MaintenanceUiState.Running;
        Result = new MaintenanceSessionResult(true, operation, false, 1, null);
        return true;
    }

    public void Complete(DeploymentOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        if (State != MaintenanceUiState.Running)
        {
            throw new InvalidOperationException("No maintenance operation is running.");
        }

        State = MaintenanceUiState.SucceededAwaitingConfirmation;
        Result = new MaintenanceSessionResult(true, outcome.Operation, true, 0, null);
    }

    public void Fail(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);

        if (State != MaintenanceUiState.Running)
        {
            throw new InvalidOperationException("No maintenance operation is running.");
        }

        State = MaintenanceUiState.Failed;
        Result = Result with
        {
            Attempted = true,
            Succeeded = false,
            ExitCode = 1,
            Error = error,
        };
    }

    public MaintenanceCloseDisposition RequestClose()
    {
        if (State == MaintenanceUiState.Running)
        {
            CloseRequested = true;
            return MaintenanceCloseDisposition.Deferred;
        }

        return MaintenanceCloseDisposition.CloseNow;
    }
}

internal interface IMaintenanceOperationService
{
    InstallationStatus GetStatus();

    Task<DeploymentOutcome> InstallOrRepairAsync();

    Task<DeploymentOutcome> UninstallAsync(bool keepSettings);
}

internal interface IMaintenanceCloseScheduler
{
    IDisposable Schedule(Control owner, TimeSpan delay, Action callback);
}
