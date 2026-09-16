using FoxMouse.Deployment;

namespace FoxMouse.Deployment.Tests;

public sealed class MaintenanceSessionControllerTests
{
    [Fact]
    public void NewSessionIsIdleAndHasNotAttemptedAnOperation()
    {
        MaintenanceSessionController session = new();

        Assert.Equal(MaintenanceUiState.Idle, session.State);
        Assert.False(session.CloseRequested);
        Assert.False(session.Result.Attempted);
        Assert.Null(session.Result.Operation);
        Assert.False(session.Result.Succeeded);
        Assert.Equal(0, session.Result.ExitCode);
        Assert.Null(session.Result.Error);
    }

    [Fact]
    public void UninstallConfirmationCanBeCancelledWithoutAttemptingAnOperation()
    {
        MaintenanceSessionController session = new();

        session.BeginConfirmation();
        Assert.Equal(MaintenanceUiState.ConfirmingUninstall, session.State);

        session.CancelConfirmation();

        Assert.Equal(MaintenanceUiState.Idle, session.State);
        Assert.False(session.Result.Attempted);
    }

    [Fact]
    public void RepeatedBeginWhileRunningIsRejectedAndPreservesFirstOperation()
    {
        MaintenanceSessionController session = new();

        Assert.True(session.BeginOperation(DeploymentOperation.Repair));
        Assert.False(session.BeginOperation(DeploymentOperation.Uninstall));

        Assert.Equal(MaintenanceUiState.Running, session.State);
        Assert.Equal(DeploymentOperation.Repair, session.Result.Operation);
    }

    [Fact]
    public void SuccessfulOperationProducesSuccessResultAndClosingState()
    {
        MaintenanceSessionController session = new();
        Assert.True(session.BeginOperation(DeploymentOperation.Install));
        DeploymentOutcome outcome = new(DeploymentOperation.Upgrade, "0.4.1", @"C:\FoxMouse");

        session.Complete(outcome);

        Assert.Equal(MaintenanceUiState.SucceededAwaitingConfirmation, session.State);
        Assert.True(session.Result.Attempted);
        Assert.Equal(DeploymentOperation.Upgrade, session.Result.Operation);
        Assert.True(session.Result.Succeeded);
        Assert.Equal(0, session.Result.ExitCode);
        Assert.Null(session.Result.Error);
        Assert.False(session.BeginOperation(DeploymentOperation.Repair));
    }

    [Fact]
    public void FailedOperationReturnsToRetryableStateWithFailureResult()
    {
        MaintenanceSessionController session = new();
        InvalidOperationException expected = new("injected failure");
        Assert.True(session.BeginOperation(DeploymentOperation.Uninstall));

        session.Fail(expected);

        Assert.Equal(MaintenanceUiState.Failed, session.State);
        Assert.True(session.Result.Attempted);
        Assert.Equal(DeploymentOperation.Uninstall, session.Result.Operation);
        Assert.False(session.Result.Succeeded);
        Assert.Equal(1, session.Result.ExitCode);
        Assert.Same(expected, session.Result.Error);

        Assert.True(session.BeginOperation(DeploymentOperation.Uninstall));
        Assert.Equal(MaintenanceUiState.Running, session.State);
        Assert.Null(session.Result.Error);
    }

    [Fact]
    public void CloseIsImmediateWhileIdleAndDeferredDuringAnOperation()
    {
        MaintenanceSessionController idle = new();
        Assert.Equal(MaintenanceCloseDisposition.CloseNow, idle.RequestClose());
        Assert.False(idle.CloseRequested);

        MaintenanceSessionController running = new();
        Assert.True(running.BeginOperation(DeploymentOperation.Repair));

        Assert.Equal(MaintenanceCloseDisposition.Deferred, running.RequestClose());
        Assert.True(running.CloseRequested);
    }

    [Fact]
    public void CompleteAndFailRequireAnActiveOperation()
    {
        MaintenanceSessionController session = new();
        DeploymentOutcome outcome = new(DeploymentOperation.Install, "0.4.1", @"C:\FoxMouse");

        Assert.Throws<InvalidOperationException>(() => session.Complete(outcome));
        Assert.Throws<InvalidOperationException>(() => session.Fail(new InvalidOperationException("not running")));
    }
}
