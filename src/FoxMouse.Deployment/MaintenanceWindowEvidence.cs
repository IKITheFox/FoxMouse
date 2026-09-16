using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace FoxMouse.Deployment;

/// <summary>
/// Captures release-gate evidence from a real maintenance-window HWND. This is
/// deliberately separate from <see cref="Control.DrawToBitmap(Bitmap, Rectangle)"/>
/// so non-client chrome, the real viewport and native scrollbars are included.
/// Callers must restrict this diagnostic surface to an isolated deployment.
/// </summary>
public static class MaintenanceWindowEvidence
{
    private const uint PrintWindowRenderFullContent = 0x00000002;

    public static void Capture(
        MaintenanceWindow window,
        string scenario,
        string? pngOutputPath,
        string? metricsOutputPath)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentException.ThrowIfNullOrWhiteSpace(scenario);
        if (window.InvokeRequired)
        {
            throw new InvalidOperationException("Maintenance-window evidence must be captured on the UI thread.");
        }

        if (!window.IsHandleCreated || !window.Visible)
        {
            throw new InvalidOperationException("The maintenance window must be visible before evidence is captured.");
        }

        if (string.IsNullOrWhiteSpace(pngOutputPath) && string.IsNullOrWhiteSpace(metricsOutputPath))
        {
            return;
        }

        window.PerformLayout();
        window.Refresh();
        Application.DoEvents();
        TryFlushDesktopComposition();

        Rectangle windowRectangle = GetWindowRectangle(window.Handle);
        using Bitmap? capture = string.IsNullOrWhiteSpace(pngOutputPath)
            ? null
            : CaptureWindow(window, windowRectangle);
        if (capture is not null)
        {
            string pngPath = PrepareOutputPath(pngOutputPath!);
            capture.Save(pngPath, ImageFormat.Png);
        }

        if (!string.IsNullOrWhiteSpace(metricsOutputPath))
        {
            string metricsPath = PrepareOutputPath(metricsOutputPath);
            WriteMetrics(window, scenario, windowRectangle, capture?.Size, metricsPath);
        }
    }

    private static Bitmap CaptureWindow(MaintenanceWindow window, Rectangle windowRectangle)
    {
        if (windowRectangle.Width <= 0 || windowRectangle.Height <= 0)
        {
            throw new InvalidOperationException("The maintenance window has invalid capture bounds.");
        }

        Bitmap bitmap = new(windowRectangle.Width, windowRectangle.Height, PixelFormat.Format32bppArgb);
        bool printed = false;
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            nint deviceContext = graphics.GetHdc();
            try
            {
                printed = PrintWindow(window.Handle, deviceContext, PrintWindowRenderFullContent);
            }
            finally
            {
                graphics.ReleaseHdc(deviceContext);
            }

            if (!printed)
            {
                graphics.CopyFromScreen(
                    windowRectangle.Location,
                    Point.Empty,
                    windowRectangle.Size,
                    CopyPixelOperation.SourceCopy | CopyPixelOperation.CaptureBlt);
            }
        }

        return bitmap;
    }

    private static void WriteMetrics(
        MaintenanceWindow window,
        string scenario,
        Rectangle windowRectangle,
        Size? bitmapSize,
        string outputPath)
    {
        Control root = FindRequired(window, "MaintenanceRootLayout");
        ScrollableControl content = (ScrollableControl)FindRequired(window, "ContentHost");
        FlowLayoutPanel footer = (FlowLayoutPanel)FindRequired(window, "ActionButtons");
        List<Control> visibleControls = EnumerateDescendants(window)
            .Where(control => control.Visible && !string.IsNullOrWhiteSpace(control.Name))
            .ToList();

        List<object> overlaps = FindSiblingOverlaps(window, visibleControls);
        List<string> clippedControls = FindClippedControls(window, visibleControls);
        List<Control> visibleFooterButtons = footer.Controls
            .Cast<Control>()
            .Where(control => control.Visible)
            .ToList();
        bool footerSingleRow = visibleFooterButtons.Count <= 1 ||
            visibleFooterButtons.Select(control => control.Top).Distinct().Count() == 1;

        Rectangle rootBounds = ToClientBounds(window, root);
        Rectangle clientBounds = window.ClientRectangle;
        Size effectiveCaptureSize = bitmapSize ?? windowRectangle.Size;
        Point clientScreenOrigin = window.PointToScreen(Point.Empty);
        Rectangle clientCaptureBounds = new(
            clientScreenOrigin.X - windowRectangle.Left,
            clientScreenOrigin.Y - windowRectangle.Top,
            clientBounds.Width,
            clientBounds.Height);
        object payload = new
        {
            schema = "foxmouse.maintenance-window/1",
            displayLanguage = FoxMouse.Core.UiText.Language,
            title = window.Text,
            scenario,
            uiState = window.UiState.ToString(),
            dpi = window.DeviceDpi,
            clientBounds = BoundsObject(clientBounds),
            captureBounds = BoundsObject(new Rectangle(Point.Empty, effectiveCaptureSize)),
            clientCaptureBounds = BoundsObject(clientCaptureBounds),
            autoScroll = window.AutoScroll,
            horizontalScrollVisible = window.HorizontalScroll.Visible,
            verticalScrollVisible = window.VerticalScroll.Visible,
            contentHorizontalScrollVisible = content.HorizontalScroll.Visible,
            contentVerticalScrollVisible = content.VerticalScroll.Visible,
            rootDock = root.Dock.ToString(),
            rootBounds = BoundsObject(rootBounds),
            visibleControlsInsideClient = clippedControls.Count == 0,
            overlapCount = overlaps.Count,
            overlaps,
            clippedControls,
            footerSingleRow,
            visibleFooterButtons = visibleFooterButtons.Select(control => control.Name).ToArray(),
            visibleControls = visibleControls.Select(control =>
            {
                Rectangle bounds = ToClientBounds(window, control);
                return new
                {
                    name = control.Name,
                    parent = control.Parent?.Name,
                    x = bounds.X,
                    y = bounds.Y,
                    width = bounds.Width,
                    height = bounds.Height,
                };
            }).ToArray(),
        };

        AtomicFilePublisher.WriteAllText(
            outputPath,
            JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static List<object> FindSiblingOverlaps(
        Form window,
        IReadOnlyCollection<Control> visibleControls)
    {
        HashSet<Control> visible = new(visibleControls);
        List<object> overlaps = [];
        foreach (Control parent in EnumerateContainers(window))
        {
            Control[] siblings = parent.Controls
                .Cast<Control>()
                .Where(visible.Contains)
                .ToArray();
            for (int leftIndex = 0; leftIndex < siblings.Length; leftIndex++)
            {
                for (int rightIndex = leftIndex + 1; rightIndex < siblings.Length; rightIndex++)
                {
                    Rectangle intersection = Rectangle.Intersect(
                        siblings[leftIndex].Bounds,
                        siblings[rightIndex].Bounds);
                    if (intersection.Width <= 0 || intersection.Height <= 0)
                    {
                        continue;
                    }

                    overlaps.Add(new
                    {
                        parent = parent.Name,
                        first = siblings[leftIndex].Name,
                        second = siblings[rightIndex].Name,
                        intersection = BoundsObject(intersection),
                    });
                }
            }
        }

        return overlaps;
    }

    private static List<string> FindClippedControls(Form window, IEnumerable<Control> visibleControls)
    {
        List<string> clipped = [];
        foreach (Control control in visibleControls)
        {
            Rectangle clientBounds = ToClientBounds(window, control);
            bool insideWindow = window.ClientRectangle.Contains(clientBounds);
            bool insideParent = control.Parent is null || control.Parent.ClientRectangle.Contains(control.Bounds);
            if (!insideWindow || !insideParent)
            {
                clipped.Add(control.Name);
            }
        }

        return clipped;
    }

    private static IEnumerable<Control> EnumerateContainers(Control root)
    {
        yield return root;
        foreach (Control child in root.Controls)
        {
            foreach (Control descendant in EnumerateContainers(child))
            {
                yield return descendant;
            }
        }
    }

    private static IEnumerable<Control> EnumerateDescendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (Control descendant in EnumerateDescendants(child))
            {
                yield return descendant;
            }
        }
    }

    private static Control FindRequired(Control root, string name)
    {
        Control[] matches = root.Controls.Find(name, searchAllChildren: true);
        return matches.Length == 1
            ? matches[0]
            : throw new InvalidOperationException($"Expected exactly one maintenance control named '{name}'.");
    }

    private static Rectangle ToClientBounds(Form window, Control control)
    {
        Point screenLocation = control.PointToScreen(Point.Empty);
        return new Rectangle(window.PointToClient(screenLocation), control.Size);
    }

    private static object BoundsObject(Rectangle bounds) => new
    {
        x = bounds.X,
        y = bounds.Y,
        width = bounds.Width,
        height = bounds.Height,
    };

    private static string PrepareOutputPath(string outputPath)
    {
        string path = Path.GetFullPath(outputPath);
        string? parent = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(parent))
        {
            throw new ArgumentException("An evidence output path must have a parent directory.", nameof(outputPath));
        }

        Directory.CreateDirectory(parent);
        return path;
    }

    private static Rectangle GetWindowRectangle(nint handle)
    {
        if (!GetWindowRect(handle, out NativeRectangle rectangle))
        {
            throw new InvalidOperationException("Could not read the maintenance window bounds.");
        }

        return Rectangle.FromLTRB(rectangle.Left, rectangle.Top, rectangle.Right, rectangle.Bottom);
    }

    private static void TryFlushDesktopComposition()
    {
        try
        {
            _ = DwmFlush();
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out NativeRectangle rectangle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(nint window, nint deviceContext, uint flags);

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmFlush();
}
