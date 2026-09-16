using FoxMouse.Core;
using FoxMouse.Platform.Windows.Configuration;
using FoxMouse.Settings.Pages;
using FoxMouse.Settings.Services;
using FoxMouse.Settings.ViewModels;
using Microsoft.UI.Xaml.Controls;
using System.Globalization;

namespace FoxMouse.Settings.Tests;

public sealed class SettingsHostViewModelTests
{
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void InvalidScaleRetainsLastValidValue(double invalid)
    {
        using SettingsHostViewModel model = new(
            new SettingsStore(Path.Combine(Path.GetTempPath(), "FoxMouse.ScaleTests", Guid.NewGuid().ToString("N"))),
            new RecordingNotifier(), new EmptyProcessCatalog());
        model.MaxScale = 4.25;
        model.MaxScale = invalid;
        Assert.Equal(4.25, model.MaxScale);
    }

    [Fact]
    public void BlockedSearchFiltersWithoutChangingStoredExclusions()
    {
        using SettingsHostViewModel model = new(
            new SettingsStore(Path.Combine(Path.GetTempPath(), "FoxMouse.SearchTests", Guid.NewGuid().ToString("N"))),
            new RecordingNotifier(), new EmptyProcessCatalog());
        Assert.True(model.AddExclusion("alpha.exe"));
        Assert.True(model.AddExclusion("beta.exe"));
        model.BlockedSearch = " ALPHA ";
        Assert.Single(model.FilteredExcludedProcesses);
        Assert.Equal(2, model.ExcludedProcesses.Count);
        Assert.Equal("1 / 2", model.BlockedSummary);
        Assert.True(model.RemoveExclusion("alpha.exe"));
        Assert.Empty(model.FilteredExcludedProcesses);
        Assert.Equal(Microsoft.UI.Xaml.Visibility.Visible, model.BlockedEmptyVisibility);
        model.BlockedSearch = "";
        Assert.Single(model.FilteredExcludedProcesses);
        Assert.Equal("beta.exe", model.FilteredExcludedProcesses[0].ExecutableName);
    }

    [Fact]
    public void MaxScaleFormatterTrimsZerosRoundsToTwoPlacesAndUsesCurrentLocale()
    {
        LocalizedTrimmedNumberFormatter formatter = new(new CultureInfo("fr-FR"));

        Assert.Equal("4", formatter.FormatDouble(4));
        Assert.Equal("4,5", formatter.FormatDouble(4.5));
        Assert.Equal("4,57", formatter.FormatDouble(4.567));
        Assert.Equal(4.5, formatter.ParseDouble("4,5"));
    }

    [Fact]
    public async Task SaveStatusIsNeutralNonClosableAndDismissesAfterTenSeconds()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "FoxMouse.Settings.Tests",
            Guid.NewGuid().ToString("N"));
        TaskCompletionSource delayGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TimeSpan? requestedDelay = null;

        Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            requestedDelay = delay;
            return delayGate.Task.WaitAsync(cancellationToken);
        }

        try
        {
            SettingsStore store = new(root);
            await store.SaveAsync(FoxMouseSettings.Default);
            RecordingNotifier notifier = new();
            using SettingsHostViewModel viewModel = new(
                store,
                notifier,
                new EmptyProcessCatalog(),
                DelayAsync);
            await viewModel.LoadAsync();
            viewModel.MaxScale = 4.2;

            await viewModel.SaveAsync();

            Assert.True(viewModel.HasStatus);
            Assert.Contains("已保存", viewModel.StatusMessage, StringComparison.Ordinal);
            Assert.Equal(InfoBarSeverity.Informational, viewModel.StatusSeverity);
            Assert.False(viewModel.IsStatusClosable);
            Assert.Equal(TimeSpan.FromSeconds(10), requestedDelay);

            TaskCompletionSource dismissed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            viewModel.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(SettingsHostViewModel.HasStatus) && !viewModel.HasStatus)
                {
                    dismissed.TrySetResult();
                }
            };

            delayGate.SetResult();
            await dismissed.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(viewModel.HasStatus);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task PreviewWithDirtySettingsSavesThenReloadsThenPreviews()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "FoxMouse.Settings.Tests",
            Guid.NewGuid().ToString("N"));

        try
        {
            SettingsStore store = new(root);
            await store.SaveAsync(FoxMouseSettings.Default);
            RecordingNotifier notifier = new();
            using SettingsHostViewModel viewModel = new(store, notifier, new EmptyProcessCatalog());
            await viewModel.LoadAsync();

            viewModel.MaxScale = 4.7;
            Assert.True(viewModel.IsDirty);

            bool previewed = await viewModel.PreviewAsync();

            Assert.True(previewed);
            Assert.Equal(["reload", "preview"], notifier.Calls);
            Assert.False(viewModel.IsDirty);
            Assert.Equal(4.7, (await store.LoadAsync()).MaxScale, precision: 6);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private sealed class RecordingNotifier : ISettingsChangeNotifier
    {
        public List<string> Calls { get; } = [];

        public Task<bool> NotifyReloadAsync(
            string settingsPath,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(Path.GetFullPath(settingsPath), settingsPath);
            Calls.Add("reload");
            return Task.FromResult(true);
        }

        public Task<bool> NotifyPreviewAsync(
            string settingsPath,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(Path.GetFullPath(settingsPath), settingsPath);
            Calls.Add("preview");
            return Task.FromResult(true);
        }
    }

    private sealed class EmptyProcessCatalog : IRunningProcessCatalog
    {
        public Task<IReadOnlyList<RunningProcessInfo>> GetProcessesAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RunningProcessInfo>>([]);
    }
}
