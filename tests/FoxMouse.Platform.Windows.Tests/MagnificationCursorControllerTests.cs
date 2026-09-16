using FoxMouse.Platform.Windows.Visibility;

namespace FoxMouse.Platform.Windows.Tests;

public sealed class MagnificationCursorControllerTests
{
    [Fact]
    public void SuccessfulShowRestoresOwnership()
    {
        FakeMagnificationApi api = new();
        using MagnificationCursorController controller = new(api);

        Assert.True(controller.TrySetVisible(false));
        Assert.True(controller.IsHiddenByThisController);
        Assert.True(controller.TrySetVisible(true));

        Assert.False(controller.IsHiddenByThisController);
        Assert.Equal([false, true], api.VisibilityTransitions);
    }

    [Fact]
    public void FailedShowLeavesOwnershipHidden()
    {
        FakeMagnificationApi api = new();
        using MagnificationCursorController controller = new(api);
        Assert.True(controller.TrySetVisible(false));
        api.FailNextVisibilityTransition = true;

        Assert.False(controller.TrySetVisible(true));

        Assert.True(controller.IsHiddenByThisController);
    }

    [Fact]
    public void DisposeWhileHiddenPerformsOneFinalRestore()
    {
        FakeMagnificationApi api = new();
        MagnificationCursorController controller = new(api);

        Assert.True(controller.TrySetVisible(false));
        controller.Dispose();

        Assert.False(controller.IsHiddenByThisController);
        Assert.Equal([false, true], api.VisibilityTransitions);
    }

    internal sealed class FakeMagnificationApi : IMagnificationCursorApi
    {
        public bool FailNextVisibilityTransition { get; set; }

        public List<bool> VisibilityTransitions { get; } = [];

        public bool Initialize() => true;

        public bool SetVisible(bool visible)
        {
            if (FailNextVisibilityTransition)
            {
                FailNextVisibilityTransition = false;
                return false;
            }

            VisibilityTransitions.Add(visible);
            return true;
        }

        public bool Uninitialize() => true;
    }
}
