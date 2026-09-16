using System.Drawing;
using System.Windows.Forms;
using FoxMouse.Deployment;

namespace FoxMouse.Deployment.Tests;

[Collection(MaintenanceUiCollection.Name)]
public sealed class MaintenanceWindowTests
{
    private const string Version = "0.4.1";

    [Theory]
    [InlineData(DeploymentOperation.Install)]
    [InlineData(DeploymentOperation.Repair)]
    [InlineData(DeploymentOperation.Upgrade)]
    [InlineData(DeploymentOperation.Uninstall)]
    public void RedesignedStatesFitAndAllResultsWaitForConfirmation(DeploymentOperation operation)
    {
        string previous = FoxMouse.Core.UiText.Language;
        try
        {
            foreach (string language in new[] { "zh-CN", "en-US" })
            {
                FoxMouse.Core.UiText.Configure(language);
                FakeMaintenanceOperations operations = new(new InstallationStatus(
                    operation == DeploymentOperation.Install ? InstallationKind.Absent : InstallationKind.Managed,
                    operation == DeploymentOperation.Upgrade ? "0.3.0" : Version));
                using MaintenanceWindowHost host = CreateHost(operations, new FakeCloseScheduler(), true);
                void Capture(string state) => host.Invoke(window =>
                {
                    AssertLayoutFitsWithoutOverlap(window);
                    string? folder = Environment.GetEnvironmentVariable("FOXMOUSE_RENDER_EVIDENCE");
                    if (string.IsNullOrEmpty(folder)) return;
                    Directory.CreateDirectory(folder);
                    using Bitmap frame = new(window.Width, window.Height);
                    window.DrawToBitmap(frame, window.ClientRectangle with { Width = window.Width, Height = window.Height });
                    frame.Save(Path.Combine(folder, $"{language}-{operation}-{state}.png"));
                });
                Capture("idle");
                if (operation == DeploymentOperation.Uninstall)
                {
                    host.Invoke(window => window.ShowUninstallConfirmationForTesting());
                    Capture("confirm");
                }
                host.Invoke(window => _ = window.BeginAutomatedOperationForTestingAsync(operation));
                Capture("running");
                if (operation == DeploymentOperation.Uninstall) operations.CompleteUninstall();
                else operations.CompleteInstall(operation);
                Assert.True(host.WaitForState(MaintenanceUiState.SucceededAwaitingConfirmation, TimeSpan.FromSeconds(3)));
                Capture("complete");
                Assert.False(host.HasExited);
                host.Invoke(window => Find<Button>(window, "CloseAction").PerformClick());
                Assert.True(host.WaitForExit(TimeSpan.FromSeconds(3)));
            }
        }
        finally { FoxMouse.Core.UiText.Configure(previous); }
    }

    [Fact]
    public void FirstScreenCanSwitchBetweenChineseAndEnglishWithoutStartingInstallation()
    {
        string previous = FoxMouse.Core.UiText.Language;
        try
        {
            FakeMaintenanceOperations operations = new(new InstallationStatus(InstallationKind.Absent, null));
            using MaintenanceWindowHost host = CreateHost(operations, new FakeCloseScheduler(), setupMode: true);
            host.Invoke(window =>
            {
                ComboBox picker = Find<ComboBox>(window, "LanguageChoice");
                Assert.True(picker.Visible);
                Assert.True(picker.Width >= 170, $"Language selector is clipped: {picker.Width}px");
                picker.SelectedIndex = 1;
                Assert.Equal("FoxMouse Setup", window.Text);
                Assert.Equal("Install FoxMouse", Find<Label>(window, "Heading").Text);
                Assert.Equal("Install", Find<Button>(window, "PrimaryAction").Text);
                Assert.Equal("Close", Find<Button>(window, "CloseAction").Text);
                Assert.False(window.SessionResult.Attempted);
                window.ClientSize = new Size(MaintenanceLayoutMetrics.MinimumClientWidth,
                    MaintenanceLayoutMetrics.MinimumClientHeight);
                window.PerformLayout();
                Assert.True(picker.Width >= 170);
                AssertLayoutFitsWithoutOverlap(window);
                picker.SelectedIndex = 0;
                Assert.Equal("安装 FoxMouse", Find<Label>(window, "Heading").Text);
                Assert.Equal("安装", Find<Button>(window, "PrimaryAction").Text);
                Assert.False(window.SessionResult.Attempted);
            });
        }
        finally { FoxMouse.Core.UiText.Configure(previous); }
    }

    [Fact]
    public void RealCloseSchedulerInvokesCallbackOnceOnTheUiThread()
    {
        StaTestThread.Run(() =>
        {
            using Form owner = new()
            {
                ShowInTaskbar = false,
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-32_000, -32_000),
            };
            MaintenanceCloseScheduler scheduler = new();
            IDisposable? registration = null;
            int callbackCount = 0;
            int uiThreadId = Environment.CurrentManagedThreadId;
            owner.Shown += (_, _) => registration = scheduler.Schedule(
                owner,
                TimeSpan.FromMilliseconds(25),
                () =>
                {
                    Assert.Equal(uiThreadId, Environment.CurrentManagedThreadId);
                    callbackCount++;
                    owner.Close();
                });

            Application.Run(owner);
            registration?.Dispose();

            Assert.Equal(1, callbackCount);
        });
    }

    [Fact]
    public void CloseButtonClosesAnIdleWindow()
    {
        FakeMaintenanceOperations operations = new(
            new InstallationStatus(InstallationKind.Absent, null));
        FakeCloseScheduler scheduler = new();
        using MaintenanceWindowHost host = CreateHost(operations, scheduler, setupMode: true);

        host.Invoke(window => Find<Button>(window, "CloseAction").PerformClick());

        Assert.True(host.WaitForExit(TimeSpan.FromSeconds(2)));
        Assert.False(host.Result.Attempted);
        Assert.Equal(0, host.Result.ExitCode);
    }

    [Fact]
    public void SuccessfulInstallIsDeduplicatedAndWaitsForConfirmation()
    {
        FakeMaintenanceOperations operations = new(
            new InstallationStatus(InstallationKind.Absent, null));
        FakeCloseScheduler scheduler = new();
        using MaintenanceWindowHost host = CreateHost(operations, scheduler, setupMode: true);

        host.Invoke(window =>
        {
            Button primary = Find<Button>(window, "PrimaryAction");
            primary.PerformClick();
            primary.PerformClick();
        });

        Assert.True(operations.InstallStarted.Wait(TimeSpan.FromSeconds(2)));
        Assert.Equal(1, operations.InstallCalls);
        operations.CompleteInstall(DeploymentOperation.Install);

        Assert.True(SpinWait.SpinUntil(
            () => host.Invoke(window => window.UiState) == MaintenanceUiState.SucceededAwaitingConfirmation,
            TimeSpan.FromSeconds(2)));
        Assert.False(scheduler.Scheduled.IsSet);
        Assert.False(host.HasExited);
        Assert.Equal("确定", host.Invoke(window => Find<Button>(window, "CloseAction").Text));

        host.Invoke(window => Find<Button>(window, "CloseAction").PerformClick());

        Assert.True(host.WaitForExit(TimeSpan.FromSeconds(2)));
        Assert.True(host.Result.Attempted);
        Assert.Equal(DeploymentOperation.Install, host.Result.Operation);
        Assert.True(host.Result.Succeeded);
        Assert.Equal(0, host.Result.ExitCode);
        Assert.Null(host.Result.Error);
    }

    [Theory]
    [InlineData(DeploymentOperation.Install)]
    [InlineData(DeploymentOperation.Repair)]
    [InlineData(DeploymentOperation.Upgrade)]
    [InlineData(DeploymentOperation.Uninstall)]
    public void SuccessfulMaintenanceUsesTheRequiredCompletionPolicy(
        DeploymentOperation operation)
    {
        FakeMaintenanceOperations operations = new(
            new InstallationStatus(InstallationKind.Managed, Version));
        FakeCloseScheduler scheduler = new();
        using MaintenanceWindowHost host = CreateHost(operations, scheduler, setupMode: true);

        host.Invoke(window => _ = window.BeginAutomatedOperationForTestingAsync(operation));
        if (operation == DeploymentOperation.Uninstall)
        {
            Assert.True(operations.UninstallStarted.Wait(TimeSpan.FromSeconds(2)));
            operations.CompleteUninstall();
        }
        else
        {
            Assert.True(operations.InstallStarted.Wait(TimeSpan.FromSeconds(2)));
            operations.CompleteInstall(operation);
        }

            Assert.True(SpinWait.SpinUntil(
                () => host.Invoke(window => window.UiState) == MaintenanceUiState.SucceededAwaitingConfirmation,
                TimeSpan.FromSeconds(2)));
            Assert.False(scheduler.Scheduled.IsSet);
            Assert.False(host.HasExited);
            host.Invoke(window => Find<Button>(window, "CloseAction").PerformClick());

        Assert.True(host.WaitForExit(TimeSpan.FromSeconds(2)));
        Assert.True(host.Result.Succeeded);
        Assert.Equal(operation, host.Result.Operation);
        Assert.Equal(0, host.Result.ExitCode);
    }

    [Fact]
    public void CloseRequestedDuringOperationIsDeferredUntilTheTransactionCompletes()
    {
        FakeMaintenanceOperations operations = new(
            new InstallationStatus(InstallationKind.Managed, Version));
        FakeCloseScheduler scheduler = new();
        using MaintenanceWindowHost host = CreateHost(operations, scheduler, setupMode: true);

        host.Invoke(window => Find<Button>(window, "RepairAction").PerformClick());
        Assert.True(operations.InstallStarted.Wait(TimeSpan.FromSeconds(2)));

        host.Invoke(window => window.RequestClose());

        Assert.False(host.HasExited);
        Assert.Equal(MaintenanceUiState.Running, host.Invoke(window => window.UiState));
        Assert.Equal(
            "完成后关闭",
            host.Invoke(window => Find<Button>(window, "CloseAction").Text));

        operations.CompleteInstall(DeploymentOperation.Repair);

        Assert.True(host.WaitForExit(TimeSpan.FromSeconds(2)));
        Assert.False(scheduler.Scheduled.IsSet);
        Assert.True(host.Result.Succeeded);
        Assert.Equal(DeploymentOperation.Repair, host.Result.Operation);
    }

    [Fact]
    public void DeferredCloseStillExitsAfterTheTransactionFailsAndRollsBack()
    {
        FakeMaintenanceOperations operations = new(
            new InstallationStatus(InstallationKind.Managed, Version));
        FakeCloseScheduler scheduler = new();
        using MaintenanceWindowHost host = CreateHost(operations, scheduler, setupMode: true);
        InvalidOperationException expected = new("injected rollback path");

        host.Invoke(window => Find<Button>(window, "RepairAction").PerformClick());
        Assert.True(operations.InstallStarted.Wait(TimeSpan.FromSeconds(2)));
        host.Invoke(window => window.RequestClose());

        operations.FailInstall(expected);

        Assert.True(host.WaitForExit(TimeSpan.FromSeconds(2)));
        Assert.False(scheduler.Scheduled.IsSet);
        Assert.True(host.Result.Attempted);
        Assert.False(host.Result.Succeeded);
        Assert.Equal(DeploymentOperation.Repair, host.Result.Operation);
        Assert.Equal(1, host.Result.ExitCode);
        Assert.Same(expected, host.Result.Error);
    }

    [Fact]
    public void FailedOperationRestoresInteractiveControlsAndCanBeClosed()
    {
        FakeMaintenanceOperations operations = new(
            new InstallationStatus(InstallationKind.Managed, Version));
        FakeCloseScheduler scheduler = new();
        using MaintenanceWindowHost host = CreateHost(operations, scheduler, setupMode: true);
        InvalidOperationException expected = new("injected maintenance failure");

        host.Invoke(window => Find<Button>(window, "RepairAction").PerformClick());
        Assert.True(operations.InstallStarted.Wait(TimeSpan.FromSeconds(2)));
        operations.FailInstall(expected);

        Assert.True(host.WaitForState(MaintenanceUiState.Failed, TimeSpan.FromSeconds(2)));
        Assert.False(host.HasExited);
        host.Invoke(window =>
        {
            Assert.True(Find<Control>(window, "FeedbackPanel").Visible);
            Assert.Contains(
                expected.Message,
                Find<Label>(window, "FeedbackMessage").Text,
                StringComparison.Ordinal);
            Assert.Empty(window.Controls.Find("Progress", searchAllChildren: true));
            Button close = Find<Button>(window, "CloseAction");
            Assert.True(close.Visible);
            Assert.True(close.Enabled);
            close.PerformClick();
        });

        Assert.True(host.WaitForExit(TimeSpan.FromSeconds(2)));
        Assert.True(host.Result.Attempted);
        Assert.False(host.Result.Succeeded);
        Assert.Equal(1, host.Result.ExitCode);
        Assert.Same(expected, host.Result.Error);
    }

    [Fact]
    public void LongFailureDetailsStayInsideTheSinglePageAndRemainAccessible()
    {
        FakeMaintenanceOperations operations = new(
            new InstallationStatus(InstallationKind.Managed, Version));
        FakeCloseScheduler scheduler = new();
        using MaintenanceWindowHost host = CreateHost(operations, scheduler, setupMode: true);
        string details = "无法访问安装包：" + new string('错', 2_200);

        host.Invoke(window => Find<Button>(window, "RepairAction").PerformClick());
        Assert.True(operations.InstallStarted.Wait(TimeSpan.FromSeconds(2)));
        operations.FailInstall(new InvalidOperationException(details));
        Assert.True(host.WaitForState(MaintenanceUiState.Failed, TimeSpan.FromSeconds(2)));

        host.Invoke(window =>
        {
            Label message = Find<Label>(window, "FeedbackMessage");
            Control card = Find<Control>(window, "FeedbackPanel");
            Assert.False(message.AutoSize);
            Assert.True(message.AutoEllipsis);
            Assert.EndsWith("…", message.Text, StringComparison.Ordinal);
            Assert.True(message.Text.Length <= 160);
            Assert.Equal(details, message.AccessibleDescription);
            Assert.Equal(MaintenanceLayoutMetrics.FeedbackCardHeight, card.Height);
            AssertLayoutFitsWithoutOverlap(window);
            Find<Button>(window, "CloseAction").PerformClick();
        });

        Assert.True(host.WaitForExit(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void UninstallConfirmationCanBeCancelledAndConfirmedInline()
    {
        FakeMaintenanceOperations operations = new(
            new InstallationStatus(InstallationKind.Managed, Version));
        FakeCloseScheduler scheduler = new();
        using MaintenanceWindowHost host = CreateHost(operations, scheduler, setupMode: false);

        host.Invoke(window => Find<Button>(window, "UninstallAction").PerformClick());

        Assert.Equal(MaintenanceUiState.ConfirmingUninstall, host.Invoke(window => window.UiState));
        Assert.Equal(0, operations.UninstallCalls);
        host.Invoke(window =>
        {
            Assert.True(Find<Control>(window, "FeedbackPanel").Visible);
            Assert.True(Find<Button>(window, "ConfirmUninstall").Visible);
            Assert.True(Find<Control>(window, "KeepSettings").Visible);
            Assert.True(Find<Control>(window, "LocationPanel").Visible);
            Assert.False(Find<Button>(window, "CloseAction").Visible);
            Assert.Equal(
                new[] { "CancelUninstall", "ConfirmUninstall" },
                Find<FlowLayoutPanel>(window, "ActionButtons").Controls
                    .Cast<Control>()
                    .Where(control => control.Visible)
                    .Select(control => control.Name));
            AssertLayoutFitsWithoutOverlap(window);
            Find<Button>(window, "CancelUninstall").PerformClick();
        });

        Assert.Equal(MaintenanceUiState.Idle, host.Invoke(window => window.UiState));
        Assert.Equal(0, operations.UninstallCalls);

        host.Invoke(window =>
        {
            Find<Button>(window, "UninstallAction").PerformClick();
            Button confirm = Find<Button>(window, "ConfirmUninstall");
            confirm.PerformClick();
            confirm.PerformClick();
        });

        Assert.True(operations.UninstallStarted.Wait(TimeSpan.FromSeconds(2)));
        Assert.Equal(1, operations.UninstallCalls);
        Assert.True(operations.LastKeepSettings);
        operations.CompleteUninstall();
        Assert.True(SpinWait.SpinUntil(
            () => host.Invoke(window => window.UiState) == MaintenanceUiState.SucceededAwaitingConfirmation,
            TimeSpan.FromSeconds(2)));
        Assert.False(scheduler.Scheduled.IsSet);
        host.Invoke(window => Find<Button>(window, "CloseAction").PerformClick());

        Assert.True(host.WaitForExit(TimeSpan.FromSeconds(2)));
        Assert.True(host.Result.Succeeded);
        Assert.Equal(DeploymentOperation.Uninstall, host.Result.Operation);
    }

    [Fact]
    public void CaptureDriverShowsConfirmationWithoutStartingUninstall()
    {
        FakeMaintenanceOperations operations = new(
            new InstallationStatus(InstallationKind.Managed, Version));
        FakeCloseScheduler scheduler = new();
        using MaintenanceWindowHost host = CreateHost(operations, scheduler, setupMode: false);

        host.Invoke(window => window.ShowUninstallConfirmationForTesting());

        Assert.Equal(MaintenanceUiState.ConfirmingUninstall, host.Invoke(window => window.UiState));
        Assert.Equal(0, operations.UninstallCalls);
        host.Invoke(window =>
        {
            Assert.True(Find<Button>(window, "ConfirmUninstall").Visible);
            Assert.True(Find<Button>(window, "CancelUninstall").Visible);
            Assert.False(Find<Control>(window, "Status").Visible);
            Find<Button>(window, "CancelUninstall").PerformClick();
            Find<Button>(window, "CloseAction").PerformClick();
        });

        Assert.True(host.WaitForExit(TimeSpan.FromSeconds(2)));
        Assert.False(host.Result.Attempted);
    }

    [Fact]
    public void ResponsiveLayoutKeepsLongPathsAndActionsInsideTheClientArea()
    {
        string longInstallRoot = @"C:\Users\Example\AppData\Local\Programs\FoxMouse\" +
            string.Join('-', Enumerable.Repeat("很长的安装路径", 20));
        FakeMaintenanceOperations operations = new(
            new InstallationStatus(InstallationKind.Managed, Version));
        FakeCloseScheduler scheduler = new();
        using MaintenanceWindowHost host = CreateHost(
            operations,
            scheduler,
            setupMode: false,
            installRoot: longInstallRoot);

        host.Invoke(window =>
        {
            window.ClientSize = new Size(
                MaintenanceLayoutMetrics.MinimumClientWidth,
                MaintenanceLayoutMetrics.MinimumClientHeight);
            window.PerformLayout();

            Assert.Equal(AutoScaleMode.Dpi, window.AutoScaleMode);
            Assert.False(window.AutoScroll);
            Assert.False(window.HorizontalScroll.Visible);
            Assert.False(window.VerticalScroll.Visible);

            TableLayoutPanel root = Find<TableLayoutPanel>(window, "MaintenanceRootLayout");
            Assert.Equal(DockStyle.Fill, root.Dock);
            Assert.False(root.AutoSize);
            Assert.Equal(MaintenanceLayoutMetrics.WindowPadding, root.Padding.Left);
            Assert.Equal(window.ClientSize.Width, root.Width);
            Assert.Equal(window.ClientSize.Height, root.Height);

            Label heading = Find<Label>(window, "Heading");
            Label description = Find<Label>(window, "Description");
            Assert.True(heading.AutoSize);
            Assert.True(description.AutoSize);
            Assert.True(description.MaximumSize.Width > 0);
            Assert.True(heading.Bottom <= description.Top);

            Label locationValue = Find<Label>(window, "LocationValue");
            Assert.Equal(longInstallRoot, locationValue.Text);
            Assert.Equal(longInstallRoot, locationValue.AccessibleDescription);
            Assert.True(locationValue.AutoEllipsis);
            Assert.True(locationValue.Right <= locationValue.Parent!.ClientRectangle.Right);

            FlowLayoutPanel actions = Find<FlowLayoutPanel>(window, "ActionButtons");
            Assert.Equal(FlowDirection.RightToLeft, actions.FlowDirection);
            Assert.False(actions.WrapContents);
            Assert.True(actions.Right <= root.DisplayRectangle.Right);
            Assert.InRange(
                root.ClientSize.Height - actions.Bottom,
                MaintenanceLayoutMetrics.WindowPadding - 2,
                MaintenanceLayoutMetrics.WindowPadding + 2);

            Assert.Empty(window.Controls.Find("Progress", searchAllChildren: true));
            Assert.IsType<MaintenanceCheckBox>(Find<Control>(window, "KeepSettings"));
            foreach (string buttonName in new[]
            {
                "PrimaryAction",
                "RepairAction",
                "UninstallAction",
                "CloseAction",
                "ConfirmUninstall",
                "CancelUninstall",
            })
            {
                MaintenanceButton button = Assert.IsType<MaintenanceButton>(
                    Find<Control>(window, buttonName));
                Assert.True(button.MinimumSize.Height >= MaintenanceLayoutMetrics.ButtonHeight);
                Assert.True(button.MinimumSize.Width >= MaintenanceLayoutMetrics.ButtonMinimumWidth);
            }

            AssertLayoutFitsWithoutOverlap(window);

            Find<Button>(window, "UninstallAction").PerformClick();
            window.PerformLayout();
            Assert.False(window.HorizontalScroll.Visible);
            Assert.False(window.VerticalScroll.Visible);
            Assert.True(Find<Control>(window, "KeepSettings").Visible);
            Assert.True(Find<Control>(window, "LocationPanel").Visible);
            AssertLayoutFitsWithoutOverlap(window);

            Find<Button>(window, "CancelUninstall").PerformClick();
            Find<Button>(window, "CloseAction").PerformClick();
        });

        Assert.True(host.WaitForExit(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void AbsentSetupCanChangeLocationThroughInjectedPicker()
    {
        FakeMaintenanceOperations operations = new(
            new InstallationStatus(InstallationKind.Absent, null));
        FakeCloseScheduler scheduler = new();
        RecordingLocationPicker picker = new(@"D:\Apps\FoxMouse");
        using MaintenanceWindowHost host = CreateHost(
            operations,
            scheduler,
            setupMode: true,
            installLocationPicker: picker);

        host.Invoke(window =>
        {
            Button change = Find<Button>(window, "ChangeLocation");
            Assert.True(change.Visible);
            Assert.True(change.Enabled);
            Assert.True(change.CanSelect, $"ChangeLocation cannot select: bounds={change.Bounds}, parent={change.Parent?.Bounds}.");
            RaiseClick(Find<Control>(window, "LocationPanel"));

            Assert.Equal(@"D:\Apps\FoxMouse", window.SelectedPaths.InstallRoot);
            Assert.Equal(@"D:\Apps\FoxMouse", operations.Paths.InstallRoot);
            Assert.Equal(@"D:\Apps\FoxMouse", Find<Label>(window, "LocationValue").Text);
            Assert.Equal(1, picker.CallCount);
            Assert.False(Find<Control>(window, "LocationError").Visible);
            Find<Button>(window, "CloseAction").PerformClick();
        });

        Assert.True(host.WaitForExit(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void CancellingTheLocationCardPickerKeepsTheCurrentInstallRoot()
    {
        FakeMaintenanceOperations operations = new(
            new InstallationStatus(InstallationKind.Absent, null));
        FakeCloseScheduler scheduler = new();
        CancellingLocationPicker picker = new();
        using MaintenanceWindowHost host = CreateHost(
            operations,
            scheduler,
            setupMode: true,
            installLocationPicker: picker);
        string originalRoot = host.Invoke(window => window.SelectedPaths.InstallRoot);

        host.Invoke(window =>
        {
            RaiseClick(Find<Control>(window, "LocationPanel"));
            Assert.Equal(1, picker.CallCount);
            Assert.Equal(originalRoot, window.SelectedPaths.InstallRoot);
            Assert.Equal(originalRoot, operations.Paths.InstallRoot);
            Assert.False(Find<Control>(window, "LocationError").Visible);
            Find<Button>(window, "CloseAction").PerformClick();
        });

        Assert.True(host.WaitForExit(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void LocationPickerFailureIsReportedInlineAndKeepsCurrentPaths()
    {
        FakeMaintenanceOperations operations = new(
            new InstallationStatus(InstallationKind.Absent, null));
        FakeCloseScheduler scheduler = new();
        ThrowingLocationPicker picker = new(new InvalidOperationException("路径不可写"));
        using MaintenanceWindowHost host = CreateHost(
            operations,
            scheduler,
            setupMode: true,
            installLocationPicker: picker);

        string originalRoot = host.Invoke(window => window.SelectedPaths.InstallRoot);
        host.Invoke(window =>
        {
            Button change = Find<Button>(window, "ChangeLocation");
            Assert.True(change.CanSelect, $"ChangeLocation cannot select: bounds={change.Bounds}, parent={change.Parent?.Bounds}.");
            change.PerformClick();
            Label error = Find<Label>(window, "LocationError");
            Assert.True(error.Visible);
            Assert.Contains("路径不可写", error.Text, StringComparison.Ordinal);
            Assert.Equal(originalRoot, window.SelectedPaths.InstallRoot);
            Assert.Equal(originalRoot, operations.Paths.InstallRoot);
            AssertLayoutFitsWithoutOverlap(window);
            Find<Button>(window, "CloseAction").PerformClick();
        });

        Assert.True(host.WaitForExit(TimeSpan.FromSeconds(2)));
    }

    private static MaintenanceWindowHost CreateHost(
        FakeMaintenanceOperations operations,
        FakeCloseScheduler scheduler,
        bool setupMode,
        string? installRoot = null,
        IInstallLocationPicker? installLocationPicker = null)
    {
        string root = installRoot ?? Path.Combine(Path.GetTempPath(), "FoxMouse-WindowTests", "FoxMouse");
        DeploymentPaths paths = new(
            root,
            Path.Combine(Path.GetTempPath(), "FoxMouse-WindowTests", "Settings"),
            Path.Combine(Path.GetTempPath(), "FoxMouse-WindowTests", "Cache"),
            Path.Combine(Path.GetTempPath(), "FoxMouse-WindowTests", "FoxMouse.lnk"),
            @"Software\FoxMouse\Tests\Window\Uninstall",
            @"Software\FoxMouse\Tests\Window\Run");
        return new MaintenanceWindowHost(() => new MaintenanceWindow(
            operations,
            paths,
            Version,
            setupMode,
            scheduler,
            installLocationPicker));
    }

    private static T Find<T>(Control root, string name)
        where T : Control
    {
        Control[] matches = root.Controls.Find(name, searchAllChildren: true);
        return Assert.IsAssignableFrom<T>(Assert.Single(matches));
    }

    private static void RaiseClick(Control control)
    {
        typeof(Control)
            .GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(control, new object[] { EventArgs.Empty });
    }

    private static void AssertLayoutFitsWithoutOverlap(Control root)
    {
        foreach (Control parent in EnumerateControls(root).Prepend(root))
        {
            Control[] visibleChildren = parent.Controls
                .Cast<Control>()
                .Where(control => control.Visible && control.Width > 0 && control.Height > 0)
                .ToArray();
            foreach (Control child in visibleChildren)
            {
                Assert.True(
                    parent.ClientRectangle.Contains(child.Bounds),
                    $"{child.Name} {child.Bounds} is clipped by {parent.Name} {parent.ClientRectangle}.");
            }

            for (int first = 0; first < visibleChildren.Length; first++)
            {
                for (int second = first + 1; second < visibleChildren.Length; second++)
                {
                    Rectangle overlap = Rectangle.Intersect(
                        visibleChildren[first].Bounds,
                        visibleChildren[second].Bounds);
                    Assert.True(
                        overlap.Width <= 0 || overlap.Height <= 0,
                        $"{visibleChildren[first].Name} overlaps {visibleChildren[second].Name} inside {parent.Name}: {overlap}.");
                }
            }
        }
    }

    private static IEnumerable<Control> EnumerateControls(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (Control descendant in EnumerateControls(child))
            {
                yield return descendant;
            }
        }
    }

    private sealed class FakeMaintenanceOperations :
        IMaintenanceOperationService,
        IMaintenancePathsUpdater
    {
        private readonly InstallationStatus _status;
        private readonly TaskCompletionSource<DeploymentOutcome> _install = NewCompletion();
        private readonly TaskCompletionSource<DeploymentOutcome> _uninstall = NewCompletion();
        private int _installCalls;
        private int _uninstallCalls;

        internal FakeMaintenanceOperations(InstallationStatus status)
        {
            _status = status;
        }

        internal ManualResetEventSlim InstallStarted { get; } = new(initialState: false);

        internal ManualResetEventSlim UninstallStarted { get; } = new(initialState: false);

        internal int InstallCalls => Volatile.Read(ref _installCalls);

        internal int UninstallCalls => Volatile.Read(ref _uninstallCalls);

        internal bool LastKeepSettings { get; private set; }

        internal DeploymentPaths Paths { get; private set; } = null!;

        public InstallationStatus GetStatus() => _status;

        public void UpdatePaths(DeploymentPaths paths) => Paths = paths;

        public Task<DeploymentOutcome> InstallOrRepairAsync()
        {
            Interlocked.Increment(ref _installCalls);
            InstallStarted.Set();
            return _install.Task;
        }

        public Task<DeploymentOutcome> UninstallAsync(bool keepSettings)
        {
            Interlocked.Increment(ref _uninstallCalls);
            LastKeepSettings = keepSettings;
            UninstallStarted.Set();
            return _uninstall.Task;
        }

        internal void CompleteInstall(DeploymentOperation operation) =>
            _install.TrySetResult(new DeploymentOutcome(operation, Version, @"C:\FoxMouse"));

        internal void FailInstall(Exception error) => _install.TrySetException(error);

        internal void CompleteUninstall() => _uninstall.TrySetResult(
            new DeploymentOutcome(DeploymentOperation.Uninstall, Version, @"C:\FoxMouse"));

        private static TaskCompletionSource<DeploymentOutcome> NewCompletion() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class RecordingLocationPicker(string installRoot) : IInstallLocationPicker
    {
        internal int CallCount { get; private set; }

        public DeploymentPaths? PickInstallLocation(IWin32Window owner, DeploymentPaths currentPaths)
        {
            Assert.NotNull(owner);
            CallCount++;
            return currentPaths with { InstallRoot = installRoot };
        }
    }

    private sealed class ThrowingLocationPicker(Exception error) : IInstallLocationPicker
    {
        public DeploymentPaths? PickInstallLocation(IWin32Window owner, DeploymentPaths currentPaths) =>
            throw error;
    }

    private sealed class CancellingLocationPicker : IInstallLocationPicker
    {
        internal int CallCount { get; private set; }

        public DeploymentPaths? PickInstallLocation(IWin32Window owner, DeploymentPaths currentPaths)
        {
            CallCount++;
            return null;
        }
    }

    private sealed class FakeCloseScheduler : IMaintenanceCloseScheduler
    {
        private Action? _callback;
        private bool _disposed;

        internal ManualResetEventSlim Scheduled { get; } = new(initialState: false);

        internal TimeSpan Delay { get; private set; }

        public IDisposable Schedule(Control owner, TimeSpan delay, Action callback)
        {
            Assert.NotNull(owner);
            Assert.NotNull(callback);
            Delay = delay;
            _callback = callback;
            Scheduled.Set();
            return new CallbackDisposable(() => _disposed = true);
        }

        internal void Fire()
        {
            Assert.False(_disposed);
            Assert.NotNull(_callback);
            _callback();
        }

        private sealed class CallbackDisposable(Action dispose) : IDisposable
        {
            private Action? _dispose = dispose;

            public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
        }
    }

    private sealed class MaintenanceWindowHost : IDisposable
    {
        private readonly Func<MaintenanceWindow> _factory;
        private readonly ManualResetEventSlim _ready = new(initialState: false);
        private readonly ManualResetEventSlim _exited = new(initialState: false);
        private readonly Thread _thread;
        private MaintenanceWindow? _window;
        private Exception? _failure;
        private MaintenanceSessionResult _result = new(false, null, false, 0, null);

        internal MaintenanceWindowHost(Func<MaintenanceWindow> factory)
        {
            _factory = factory;
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "FoxMouse maintenance window integration test",
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();

            int signalled = WaitHandle.WaitAny(
                new[] { _ready.WaitHandle, _exited.WaitHandle },
                TimeSpan.FromSeconds(5));
            if (signalled == WaitHandle.WaitTimeout)
            {
                throw new TimeoutException("The maintenance window did not become ready within five seconds.");
            }

            ThrowIfFailed();
        }

        internal bool HasExited => _exited.IsSet;

        internal MaintenanceSessionResult Result
        {
            get
            {
                Assert.True(_exited.IsSet, "The window must exit before its final session result is read.");
                ThrowIfFailed();
                return _result;
            }
        }

        internal void Invoke(Action<MaintenanceWindow> action) =>
            Invoke(window =>
            {
                action(window);
                return true;
            });

        internal T Invoke<T>(Func<MaintenanceWindow, T> action)
        {
            ArgumentNullException.ThrowIfNull(action);
            MaintenanceWindow window = _window ?? throw new InvalidOperationException("The window is unavailable.");
            T? result = default;
            Exception? failure = null;
            using ManualResetEventSlim finished = new(initialState: false);
            window.BeginInvoke(new Action(() =>
            {
                try
                {
                    result = action(window);
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
                finally
                {
                    finished.Set();
                }
            }));

            if (!finished.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("The maintenance window UI thread did not process a test action.");
            }

            if (failure is not null)
            {
                throw failure;
            }

            return result!;
        }

        internal bool WaitForState(MaintenanceUiState state, TimeSpan timeout)
        {
            DateTime deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline && !_exited.IsSet)
            {
                if (Invoke(window => window.UiState) == state)
                {
                    return true;
                }

                Thread.Sleep(10);
            }

            return false;
        }

        internal bool WaitForExit(TimeSpan timeout)
        {
            bool exited = _exited.Wait(timeout);
            if (exited)
            {
                Assert.True(_thread.Join(TimeSpan.FromSeconds(1)));
                ThrowIfFailed();
            }

            return exited;
        }

        public void Dispose()
        {
            if (!_exited.IsSet && _window is not null)
            {
                try
                {
                    Invoke(window => window.RequestClose());
                }
                catch (InvalidOperationException)
                {
                }
            }

            if (!_exited.Wait(TimeSpan.FromSeconds(3)))
            {
                throw new TimeoutException("The maintenance window did not exit during test cleanup.");
            }

            _thread.Join(TimeSpan.FromSeconds(1));
            _ready.Dispose();
            _exited.Dispose();
        }

        private void Run()
        {
            try
            {
                using MaintenanceWindow window = _factory();
                _window = window;
                window.ShowInTaskbar = false;
                window.StartPosition = FormStartPosition.Manual;
                window.Location = new Point(-32_000, -32_000);
                window.Shown += (_, _) => _ready.Set();
                Application.Run(window);
                _result = window.SessionResult;
            }
            catch (Exception exception)
            {
                _failure = exception;
            }
            finally
            {
                _exited.Set();
            }
        }

        private void ThrowIfFailed()
        {
            if (_failure is not null)
            {
                throw new InvalidOperationException("The maintenance window test host failed.", _failure);
            }
        }
    }
}
