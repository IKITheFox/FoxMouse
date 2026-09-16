using FoxMouse.Platform.Windows.Visibility;

namespace FoxMouse.Platform.Windows.Tests;

public sealed class FakeSystemCursorControllerTests
{
    [Fact]
    public void HideThenShowRecordsTransitionsAndRestoresState()
    {
        using FakeSystemCursorController controller = new();

        Assert.True(controller.TrySetVisible(false));
        Assert.True(controller.IsInitialized);
        Assert.True(controller.IsHiddenByThisController);

        Assert.True(controller.TrySetVisible(true));

        Assert.False(controller.IsHiddenByThisController);
        Assert.Equal([false, true], controller.VisibilityTransitions);
    }

    [Fact]
    public void DisposeWhileHiddenAddsExactlyOneRecoveryTransition()
    {
        FakeSystemCursorController controller = new();
        Assert.True(controller.TrySetVisible(false));

        controller.Dispose();
        controller.Dispose();

        Assert.False(controller.IsHiddenByThisController);
        Assert.Equal([false, true], controller.VisibilityTransitions);
        Assert.Throws<ObjectDisposedException>(() => controller.TryInitialize());
        Assert.Throws<ObjectDisposedException>(() => controller.TrySetVisible(false));
    }

    [Fact]
    public void DisposeWhileAlreadyVisibleDoesNotInventARecoveryTransition()
    {
        FakeSystemCursorController controller = new();
        Assert.True(controller.TrySetVisible(true));

        controller.Dispose();

        Assert.Equal([true], controller.VisibilityTransitions);
    }

    [Fact]
    public void FailedInitializationDoesNotChangeVisibilityState()
    {
        using FakeSystemCursorController controller = new()
        {
            FailInitialization = true,
        };

        Assert.False(controller.TryInitialize());
        Assert.False(controller.TrySetVisible(false));
        Assert.False(controller.IsInitialized);
        Assert.False(controller.IsHiddenByThisController);
        Assert.Empty(controller.VisibilityTransitions);
    }

    [Fact]
    public void FailedTransitionIsOneShotAndLeavesStateUnchanged()
    {
        using FakeSystemCursorController controller = new()
        {
            FailNextTransition = true,
        };

        Assert.False(controller.TrySetVisible(false));
        Assert.False(controller.IsHiddenByThisController);
        Assert.Empty(controller.VisibilityTransitions);

        Assert.True(controller.TrySetVisible(false));
        Assert.True(controller.IsHiddenByThisController);
        Assert.Equal([false], controller.VisibilityTransitions);
    }
}
