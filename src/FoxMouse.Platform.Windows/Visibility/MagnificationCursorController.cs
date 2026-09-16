using System.Runtime.InteropServices;

namespace FoxMouse.Platform.Windows.Visibility;

public sealed class MagnificationCursorController : ISystemCursorController
{
    private readonly IMagnificationCursorApi _api;
    private bool _disposed;

    public MagnificationCursorController()
        : this(new Win32MagnificationCursorApi())
    {
    }

    internal MagnificationCursorController(IMagnificationCursorApi api) =>
        _api = api ?? throw new ArgumentNullException(nameof(api));

    public bool IsInitialized { get; private set; }

    public bool IsHiddenByThisController { get; private set; }

    public bool TryInitialize()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsInitialized)
        {
            return true;
        }

        try
        {
            IsInitialized = _api.Initialize();
        }
        catch (DllNotFoundException)
        {
            IsInitialized = false;
        }
        catch (EntryPointNotFoundException)
        {
            IsInitialized = false;
        }

        return IsInitialized;
    }

    public bool TrySetVisible(bool visible)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!TryInitialize())
        {
            return false;
        }

        bool result;
        try
        {
            result = _api.SetVisible(visible);
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }

        if (result)
        {
            IsHiddenByThisController = !visible;
        }

        return result;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (IsHiddenByThisController)
        {
            if (!_api.SetVisible(true))
            {
                return;
            }

            IsHiddenByThisController = false;
        }

        if (IsInitialized)
        {
            _ = _api.Uninitialize();
            IsInitialized = false;
        }

        _disposed = true;
        GC.SuppressFinalize(this);
    }
}

internal interface IMagnificationCursorApi
{
    bool Initialize();

    bool SetVisible(bool visible);

    bool Uninitialize();
}

internal sealed class Win32MagnificationCursorApi : IMagnificationCursorApi
{
    public bool Initialize() => MagInitialize();

    public bool SetVisible(bool visible) => MagShowSystemCursor(visible);

    public bool Uninitialize() => MagUninitialize();

    [DllImport("Magnification.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MagInitialize();

    [DllImport("Magnification.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MagUninitialize();

    [DllImport("Magnification.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MagShowSystemCursor([MarshalAs(UnmanagedType.Bool)] bool showCursor);
}
