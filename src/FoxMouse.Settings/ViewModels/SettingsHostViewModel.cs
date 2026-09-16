using System.Collections.ObjectModel;
using FoxMouse.Core;
using FoxMouse.Platform.Windows.Configuration;
using FoxMouse.Settings.Services;
using Microsoft.UI.Xaml.Controls;

namespace FoxMouse.Settings.ViewModels;

public sealed class SettingsHostViewModel : ObservableObject, IDisposable
{
    private readonly SettingsStore _settingsStore;
    private readonly ISettingsChangeNotifier _notifier;
    private readonly IRunningProcessCatalog _processCatalog;
    private readonly Func<TimeSpan, CancellationToken, Task> _statusDelayAsync;
    private readonly List<RunningProcessItemViewModel> _allProcesses = [];
    private CancellationTokenSource? _refreshCancellation;
    private CancellationTokenSource? _statusDismissCancellation;
    private long _statusRevision;
    private bool _enabled;
    private int _languageIndex;
    private int _modeIndex;
    private double _sensitivity;
    private double _maxScale;
    private bool _startWithWindows;
    private bool _disableWhileDragging;
    private bool _pauseInFullscreen;
    private bool _isDirty;
    private bool _isBusy;
    private bool _hasStatus;
    private string _statusMessage = string.Empty;
    private InfoBarSeverity _statusSeverity = InfoBarSeverity.Informational;
    private bool _isStatusClosable = true;
    private string _processSearch = string.Empty;
    private string _blockedSearch = string.Empty;
    private bool _includeBackgroundProcesses;
    private bool _processesLoaded;
    private bool _disposed;

    public SettingsHostViewModel(
        SettingsStore settingsStore,
        ISettingsChangeNotifier notifier,
        IRunningProcessCatalog processCatalog)
        : this(
            settingsStore,
            notifier,
            processCatalog,
            static (delay, cancellationToken) => Task.Delay(delay, cancellationToken))
    {
    }

    internal SettingsHostViewModel(
        SettingsStore settingsStore,
        ISettingsChangeNotifier notifier,
        IRunningProcessCatalog processCatalog,
        Func<TimeSpan, CancellationToken, Task> statusDelayAsync)
    {
        _settingsStore = settingsStore;
        _notifier = notifier;
        _processCatalog = processCatalog;
        _statusDelayAsync = statusDelayAsync;
        ExcludedProcesses.CollectionChanged += (_, _) => ApplyBlockedFilter();
    }

    public ObservableCollection<RunningProcessItemViewModel> FilteredProcesses { get; } = [];

    public ObservableCollection<ExcludedProcessItemViewModel> ExcludedProcesses { get; } = [];
    public ObservableCollection<ExcludedProcessItemViewModel> FilteredExcludedProcesses { get; } = [];

    public string BlockedSearch
    {
        get => _blockedSearch;
        set
        {
            if (SetProperty(ref _blockedSearch, value ?? string.Empty)) ApplyBlockedFilter();
        }
    }

    public string BlockedSummary => $"{FilteredExcludedProcesses.Count} / {ExcludedProcesses.Count}";
    public Microsoft.UI.Xaml.Visibility BlockedEmptyVisibility => FilteredExcludedProcesses.Count == 0
        ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    private void ApplyBlockedFilter()
    {
        string query = BlockedSearch.Trim();
        FilteredExcludedProcesses.Clear();
        foreach (ExcludedProcessItemViewModel item in ExcludedProcesses.Where(item =>
            item.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
            item.ExecutableName.Contains(query, StringComparison.OrdinalIgnoreCase)))
        {
            FilteredExcludedProcesses.Add(item);
        }
        OnPropertyChanged(nameof(BlockedSummary));
        OnPropertyChanged(nameof(BlockedEmptyVisibility));
    }

    public bool Enabled
    {
        get => _enabled;
        set => SetSetting(ref _enabled, value);
    }

    public int LanguageIndex
    {
        get => _languageIndex;
        set => SetSetting(ref _languageIndex, Math.Clamp(value, 0, 2));
    }

    public int ModeIndex
    {
        get => _modeIndex;
        set => SetSetting(ref _modeIndex, Math.Clamp(value, 0, 1));
    }

    public double Sensitivity
    {
        get => _sensitivity;
        set
        {
            if (SetSetting(ref _sensitivity, Math.Clamp(value, 0, 1)))
            {
                OnPropertyChanged(nameof(SensitivityLabel));
            }
        }
    }

    public string SensitivityLabel => Sensitivity switch
    {
        < 0.34 => FoxMouse.Core.UiText.Get("Low"),
        < 0.67 => FoxMouse.Core.UiText.Get("Medium"),
        _ => FoxMouse.Core.UiText.Get("High"),
    };

    public double MaxScale
    {
        get => _maxScale;
        set
        {
            // NumberBox uses NaN for an empty editor. Keep the last valid
            // setting while the user replaces text; never store that sentinel.
            if (!double.IsFinite(value)) return;
            SetSetting(ref _maxScale,
                Math.Clamp(value, SettingsNormalizer.MinimumScale, SettingsNormalizer.MaximumScale));
        }
    }

    public bool StartWithWindows
    {
        get => _startWithWindows;
        set => SetSetting(ref _startWithWindows, value);
    }

    public bool DisableWhileDragging
    {
        get => _disableWhileDragging;
        set => SetSetting(ref _disableWhileDragging, value);
    }

    public bool PauseInFullscreen
    {
        get => _pauseInFullscreen;
        set => SetSetting(ref _pauseInFullscreen, value);
    }

    public bool IsDirty
    {
        get => _isDirty;
        private set
        {
            if (SetProperty(ref _isDirty, value))
            {
                OnPropertyChanged(nameof(CanSave));
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanSave));
            }
        }
    }

    public bool CanSave => IsDirty && !IsBusy;

    public bool HasStatus
    {
        get => _hasStatus;
        private set => SetProperty(ref _hasStatus, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public InfoBarSeverity StatusSeverity
    {
        get => _statusSeverity;
        private set => SetProperty(ref _statusSeverity, value);
    }

    public bool IsStatusClosable
    {
        get => _isStatusClosable;
        private set => SetProperty(ref _isStatusClosable, value);
    }

    public string ProcessSearch
    {
        get => _processSearch;
        set
        {
            if (SetProperty(ref _processSearch, value ?? string.Empty))
            {
                ApplyProcessFilter();
            }
        }
    }

    public bool ProcessesLoaded
    {
        get => _processesLoaded;
        private set => SetProperty(ref _processesLoaded, value);
    }

    public bool IncludeBackgroundProcesses
    {
        get => _includeBackgroundProcesses;
        set
        {
            if (SetProperty(ref _includeBackgroundProcesses, value))
            {
                ApplyProcessFilter();
            }
        }
    }

    public string SettingsDirectory => _settingsStore.RootDirectory;

    public string SettingsPath => _settingsStore.SettingsPath;

    public string Version => FoxMouse.Core.ProductRelease.DisplayVersion;

    public static SettingsHostViewModel CreateDefault(string? rootDirectory = null) => new(
        new SettingsStore(rootDirectory),
        rootDirectory is null ? new SettingsChangeNotifier() : new IsolatedSettingsChangeNotifier(),
        new RunningProcessCatalog());

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        IsBusy = true;
        try
        {
            FoxMouseSettings settings = SettingsNormalizer.Normalize(
                await _settingsStore.LoadAsync(cancellationToken));
            ApplySettings(settings);
            IsDirty = false;
            ClearStatus();
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<bool> SaveAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        IsBusy = true;
        try
        {
            return await SaveCoreAsync(reportStatus: true, cancellationToken);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<bool> PreviewAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (IsBusy)
        {
            return false;
        }

        IsBusy = true;
        try
        {
            bool reloadNotified = true;
            if (IsDirty)
            {
                reloadNotified = await SaveCoreAsync(reportStatus: false, cancellationToken);
            }

            bool notified = await _notifier.NotifyPreviewAsync(_settingsStore.SettingsPath, cancellationToken);
            SetStatus(
                notified
                    ? reloadNotified
                        ? FoxMouse.Core.UiText.Get("PreviewRequested")
                        : FoxMouse.Core.UiText.Get("PreviewReloadUnconfirmed")
                    : FoxMouse.Core.UiText.Get("PreviewNotRunning"),
                notified ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
            return notified;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<bool> SaveCoreAsync(
        bool reportStatus,
        CancellationToken cancellationToken)
    {
        FoxMouseSettings settings = BuildSettings();
        await _settingsStore.SaveAsync(settings, cancellationToken);
        bool notified = await _notifier.NotifyReloadAsync(_settingsStore.SettingsPath, cancellationToken);
        IsDirty = false;
        if (reportStatus)
        {
            SetStatus(
                notified
                    ? FoxMouse.Core.UiText.Get("SavedApplied")
                    : FoxMouse.Core.UiText.Get("SavedNextStart"),
                InfoBarSeverity.Informational,
                isClosable: false,
                autoDismissAfter: TimeSpan.FromSeconds(10));
        }

        return notified;
    }

    public async Task RefreshProcessesAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        _refreshCancellation?.Cancel();
        _refreshCancellation?.Dispose();
        _refreshCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        IsBusy = true;
        try
        {
            IReadOnlyList<RunningProcessInfo> processes =
                await _processCatalog.GetProcessesAsync(_refreshCancellation.Token);
            _allProcesses.Clear();
            _allProcesses.AddRange(processes.Select(process => new RunningProcessItemViewModel(process)));
            ProcessesLoaded = true;
            RefreshExclusionStates();
            ApplyProcessFilter();
        }
        catch (OperationCanceledException) when (_refreshCancellation.IsCancellationRequested)
        {
        }
        finally
        {
            IsBusy = false;
        }
    }

    public bool AddExclusion(string? candidate)
    {
        ThrowIfDisposed();
        if (!ProcessExclusionName.TryNormalize(candidate, out string executableName))
        {
            SetStatus(FoxMouse.Core.UiText.Get("ChooseExe"), InfoBarSeverity.Warning);
            return false;
        }

        if (ExcludedProcesses.Any(item =>
                item.ExecutableName.Equals(executableName, StringComparison.OrdinalIgnoreCase)))
        {
            SetStatus(FoxMouse.Core.UiText.Format("AlreadyBlocked", executableName), InfoBarSeverity.Informational);
            return false;
        }

        if (ExcludedProcesses.Count >= 128)
        {
            SetStatus(FoxMouse.Core.UiText.Get("BlockedLimit"), InfoBarSeverity.Warning);
            return false;
        }

        ExcludedProcesses.Add(new ExcludedProcessItemViewModel(executableName));
        IsDirty = true;
        RefreshExclusionStates();
        ApplyProcessFilter();
        return true;
    }

    public bool RemoveExclusion(string? executableName)
    {
        ThrowIfDisposed();
        ExcludedProcessItemViewModel? item = ExcludedProcesses.FirstOrDefault(candidate =>
            candidate.ExecutableName.Equals(executableName, StringComparison.OrdinalIgnoreCase));
        if (item is null)
        {
            return false;
        }

        ExcludedProcesses.Remove(item);
        IsDirty = true;
        RefreshExclusionStates();
        ApplyProcessFilter();
        return true;
    }

    public void ReportError(string message) => SetStatus(message, InfoBarSeverity.Error);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CancelStatusDismissal();
        _refreshCancellation?.Cancel();
        _refreshCancellation?.Dispose();
        _refreshCancellation = null;
    }

    private void ApplySettings(FoxMouseSettings settings)
    {
        _enabled = settings.Enabled;
        _languageIndex = settings.Language switch { "zh-CN" => 1, "en-US" => 2, _ => 0 };
        _modeIndex = settings.Mode == CursorEffectMode.Compatibility ? 1 : 0;
        _sensitivity = settings.Sensitivity;
        _maxScale = settings.MaxScale;
        _startWithWindows = settings.StartWithWindows;
        _disableWhileDragging = settings.DisableWhileDragging;
        _pauseInFullscreen = settings.PauseInFullscreen;

        OnPropertyChanged(nameof(Enabled));
        OnPropertyChanged(nameof(LanguageIndex));
        OnPropertyChanged(nameof(ModeIndex));
        OnPropertyChanged(nameof(Sensitivity));
        OnPropertyChanged(nameof(SensitivityLabel));
        OnPropertyChanged(nameof(MaxScale));
        OnPropertyChanged(nameof(StartWithWindows));
        OnPropertyChanged(nameof(DisableWhileDragging));
        OnPropertyChanged(nameof(PauseInFullscreen));

        ExcludedProcesses.Clear();
        foreach (string process in settings.ExcludedProcesses)
        {
            if (ProcessExclusionName.TryNormalize(process, out string executableName))
            {
                ExcludedProcesses.Add(new ExcludedProcessItemViewModel(executableName));
            }
        }

        RefreshExclusionStates();
        ApplyProcessFilter();
    }

    private FoxMouseSettings BuildSettings() => SettingsNormalizer.Normalize(new FoxMouseSettings
    {
        Enabled = Enabled,
        Language = LanguageIndex switch { 1 => "zh-CN", 2 => "en-US", _ => "system" },
        Mode = ModeIndex == 1 ? CursorEffectMode.Compatibility : CursorEffectMode.HighFidelity,
        Sensitivity = Sensitivity,
        MaxScale = MaxScale,
        StartWithWindows = StartWithWindows,
        DisableWhileDragging = DisableWhileDragging,
        PauseInFullscreen = PauseInFullscreen,
        ExcludedProcesses = ExcludedProcesses.Select(item => item.ExecutableName).ToArray(),
    });

    private bool SetSetting<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (!SetProperty(ref field, value, propertyName))
        {
            return false;
        }

        IsDirty = true;
        return true;
    }

    private void RefreshExclusionStates()
    {
        HashSet<string> exclusions = ExcludedProcesses
            .Select(item => item.ExecutableName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (RunningProcessItemViewModel process in _allProcesses)
        {
            process.IsExcluded = exclusions.Contains(process.ExecutableName);
        }
    }

    private void ApplyProcessFilter()
    {
        string query = ProcessSearch.Trim();
        IEnumerable<RunningProcessItemViewModel> matches = _allProcesses
            .Where(process => IncludeBackgroundProcesses || process.IsVisibleApplication);
        if (query.Length > 0)
        {
            matches = matches.Where(process =>
                process.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                process.ExecutableName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                process.WindowTitle.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                (process.FullPath?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        FilteredProcesses.Clear();
        foreach (RunningProcessItemViewModel process in matches
                     .OrderByDescending(process => process.IsVisibleApplication)
                     .ThenBy(process => process.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                     .Take(200))
        {
            FilteredProcesses.Add(process);
        }
    }

    private void SetStatus(
        string message,
        InfoBarSeverity severity,
        bool isClosable = true,
        TimeSpan? autoDismissAfter = null)
    {
        CancelStatusDismissal();
        long revision = ++_statusRevision;
        HasStatus = false;
        StatusMessage = message;
        StatusSeverity = severity;
        IsStatusClosable = isClosable;
        HasStatus = true;

        if (autoDismissAfter is not TimeSpan delay)
        {
            return;
        }

        _statusDismissCancellation = new CancellationTokenSource();
        _ = DismissStatusAsync(revision, delay, _statusDismissCancellation.Token);
    }

    private async Task DismissStatusAsync(
        long revision,
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        try
        {
            await _statusDelayAsync(delay, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        if (_disposed || revision != _statusRevision)
        {
            return;
        }

        HasStatus = false;
        _statusDismissCancellation?.Dispose();
        _statusDismissCancellation = null;
    }

    private void ClearStatus()
    {
        CancelStatusDismissal();
        _statusRevision++;
        HasStatus = false;
        IsStatusClosable = true;
    }

    private void CancelStatusDismissal()
    {
        CancellationTokenSource? cancellation = _statusDismissCancellation;
        _statusDismissCancellation = null;
        if (cancellation is null)
        {
            return;
        }

        cancellation.Cancel();
        cancellation.Dispose();
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
