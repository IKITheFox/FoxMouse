using FoxMouse.Settings.Services;

namespace FoxMouse.Settings.ViewModels;

public sealed class RunningProcessItemViewModel(RunningProcessInfo process) : ObservableObject
{
    private bool _isExcluded;

    public string DisplayName { get; } = process.DisplayName;

    public string ExecutableName { get; } = process.ExecutableName;

    public string? FullPath { get; } = process.FullPath;

    public string WindowTitle { get; } = process.WindowTitle;

    public bool IsVisibleApplication { get; } = process.IsVisibleApplication;

    public string DetailLabel => string.IsNullOrWhiteSpace(WindowTitle)
        ? $"{ExecutableName} · {InstanceLabel}"
        : $"{ExecutableName} · {WindowTitle}";

    public string InstanceLabel => process.InstanceCount == 1
        ? FoxMouse.Core.UiText.Get("OneInstance")
        : FoxMouse.Core.UiText.Format("Instances", process.InstanceCount);

    public string Monogram => string.IsNullOrWhiteSpace(DisplayName)
        ? "?"
        : DisplayName[..1].ToUpperInvariant();

    public bool IsExcluded
    {
        get => _isExcluded;
        set
        {
            if (SetProperty(ref _isExcluded, value))
            {
                OnPropertyChanged(nameof(CanAdd));
                OnPropertyChanged(nameof(ActionLabel));
            }
        }
    }

    public bool CanAdd => !IsExcluded;

    public string ActionLabel => IsExcluded ? FoxMouse.Core.UiText.Get("Added") : FoxMouse.Core.UiText.Get("Add");
}

public sealed class ExcludedProcessItemViewModel(string executableName)
{
    public string ExecutableName { get; } = executableName;

    public string DisplayName => Path.GetFileNameWithoutExtension(ExecutableName);

    public string Monogram => string.IsNullOrWhiteSpace(DisplayName)
        ? "?"
        : DisplayName[..1].ToUpperInvariant();
}
