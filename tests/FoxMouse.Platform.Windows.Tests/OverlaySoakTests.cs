using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using FoxMouse.Platform.Windows.Cursor;
using FoxMouse.Platform.Windows.Rendering;
using Xunit.Abstractions;

namespace FoxMouse.Platform.Windows.Tests;

[Collection(OverlaySoakCollection.Name)]
[Trait("Category", "Performance")]
public sealed class OverlaySoakTests
{
    private const int WarmupFrames = 48;
    private const int MeasuredFrames = 600;
    private const double MaximumMeanMilliseconds = 4d;
    private const double MaximumP95Milliseconds = 8d;
    private const int HighResolutionMeasuredFrames = 240;
    private const double HighResolutionMaximumMeanMilliseconds = 12d;
    private const double HighResolutionMaximumP95Milliseconds = 24d;
    private const int GrGdiObjects = 0;
    private const int GrUserObjects = 1;
    private static readonly TimeSpan TestHostWarmupObservation = TimeSpan.FromMilliseconds(1_250);
    private static readonly TimeSpan ResourceSettleObservation = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan ResourceSettleTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ResourceSampleInterval = TimeSpan.FromMilliseconds(50);
    private const int RequiredStableResourceSamples = 4;
    private readonly ITestOutputHelper _output;

    public OverlaySoakTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [WindowsDesktopFact]
    public void LocatorRenderingMeetsPerformanceAndResourceBudgets()
    {
        StaTestThread.Run(() =>
        {
            System.Drawing.Point position = System.Windows.Forms.Cursor.Position;
            RunSoak(
                "locator",
                (overlay, frame) => overlay.ShowLocator(
                    position,
                    progress: (frame % 120) / 119d));
        });
    }

    [WindowsDesktopFact]
    public void ReplaceableCursorRenderingMeetsPerformanceAndResourceBudgets()
    {
        StaTestThread.Run(() =>
        {
            // This test only observes/copies the current cursor and draws an
            // overlay. It never constructs a system-cursor visibility controller.
            using CursorTracker tracker = new();
            CursorImage? deterministicImage = null;
            if (!tracker.TryObserve(out CursorObservation observation) ||
                observation is not { IsVisible: true, Image.SupportsReplacement: true })
            {
                deterministicImage = CreateDeterministicCursorImage();
                observation = new CursorObservation(
                    System.Windows.Forms.Cursor.Position,
                    IsVisible: true,
                    deterministicImage);
                _output.WriteLine(
                    "The desktop cursor was not replaceable; the deterministic color-raster fixture was used.");
            }

            try
            {
                RunSoak(
                    deterministicImage is null ? "current-cursor" : "deterministic-cursor",
                    (overlay, frame) => overlay.ShowCursor(
                        observation,
                        scale: 1d + (5d * ((frame % 120) / 119d))));
            }
            finally
            {
                deterministicImage?.Dispose();
            }
        });
    }

    [WindowsDesktopFact]
    public void HighResolutionCursorFixtureMeetsPerformanceAndResourceBudgets()
    {
        StaTestThread.Run(() =>
        {
            using CursorImage image = CreateHighResolutionCursorImage();
            CursorObservation observation = new(
                new Point(-3_840, -2_160),
                IsVisible: true,
                image);

            RunSoak(
                "high-resolution-8k-desktop-cursor",
                (overlay, frame) => overlay.ShowCursor(
                    observation,
                    scale: 1d + (5d * ((frame % 120) / 119d)),
                    preparedMaximumScale: 6d),
                measuredFrames: HighResolutionMeasuredFrames,
                maximumMeanMilliseconds: HighResolutionMaximumMeanMilliseconds,
                maximumP95Milliseconds: HighResolutionMaximumP95Milliseconds);
        });
    }

    private static CursorImage CreateDeterministicCursorImage()
    {
        Bitmap bitmap = new(32, 32, PixelFormat.Format32bppPArgb);
        using Graphics graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Transparent);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        Point[] arrow =
        [
            new(3, 2),
            new(3, 25),
            new(9, 19),
            new(14, 29),
            new(20, 26),
            new(15, 17),
            new(24, 17),
        ];
        using GraphicsPath path = new();
        path.AddPolygon(arrow);
        using SolidBrush fill = new(Color.FromArgb(255, 232, 88, 45));
        using Pen outline = new(Color.White, 2f);
        graphics.FillPath(fill, path);
        graphics.DrawPath(outline, path);
        return new CursorImage((nint)(-1), bitmap, new Point(3, 2), supportsReplacement: true);
    }

    private static CursorImage CreateHighResolutionCursorImage()
    {
        Bitmap bitmap = new(768, 768, PixelFormat.Format32bppPArgb);
        using Graphics graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Transparent);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        Point[] arrow =
        [
            new(72, 48),
            new(72, 624),
            new(216, 480),
            new(336, 720),
            new(480, 648),
            new(360, 432),
            new(600, 432),
        ];
        using GraphicsPath path = new();
        path.AddPolygon(arrow);
        using SolidBrush fill = new(Color.FromArgb(255, 232, 88, 45));
        using Pen outline = new(Color.White, 24f);
        graphics.FillPath(fill, path);
        graphics.DrawPath(outline, path);
        return new CursorImage(
            new nint(-2),
            bitmap,
            new Size(128, 128),
            new Point(12, 8),
            supportsReplacement: true,
            CursorRenderQuality.ResourceMatched);
    }

    private void RunSoak(
        string scenario,
        Action<LayeredCursorOverlay, int> renderFrame,
        int measuredFrames = MeasuredFrames,
        double maximumMeanMilliseconds = MaximumMeanMilliseconds,
        double maximumP95Milliseconds = MaximumP95Milliseconds)
    {
        ArgumentNullException.ThrowIfNull(renderFrame);
        double[] elapsedMilliseconds = new double[measuredFrames];
        using Process process = Process.GetCurrentProcess();

        // Prime an entire short overlay lifecycle before taking the process
        // baseline. A single frame does not trigger every WinForms/test-host
        // one-time handle allocation seen during the real warm-up loop.
        using (LayeredCursorOverlay primingOverlay = new())
        {
            for (int frame = 0; frame < WarmupFrames; frame++)
            {
                renderFrame(primingOverlay, frame);
            }

            primingOverlay.HideOverlay();
        }

        ForceFullCollection();
        ResourceSnapshot processBaseline = CaptureSettledResources(process, TestHostWarmupObservation);
        ResourceSnapshot steadyBaseline;
        ResourceSnapshot steadyAfter;

        using (LayeredCursorOverlay overlay = new())
        {
            try
            {
                for (int frame = 0; frame < WarmupFrames; frame++)
                {
                    renderFrame(overlay, frame);
                }

                ForceFullCollection();
                steadyBaseline = CaptureSettledResources(process, ResourceSettleObservation);
                for (int frame = 0; frame < measuredFrames; frame++)
                {
                    long started = Stopwatch.GetTimestamp();
                    renderFrame(overlay, frame + WarmupFrames);
                    elapsedMilliseconds[frame] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                }

                ForceFullCollection();
                steadyAfter = CaptureSettledResources(process, ResourceSettleObservation);
            }
            finally
            {
                overlay.HideOverlay();
            }
        }

        ForceFullCollection();
        ResourceSnapshot processAfterCleanup = CaptureSettledResources(process, ResourceSettleObservation);
        TimingDistribution timings = TimingDistribution.From(elapsedMilliseconds);
        _output.WriteLine(
            $"{scenario}: frames={measuredFrames}; mean={timings.MeanMilliseconds:F3} ms; " +
            $"p50={timings.P50Milliseconds:F3} ms; p95={timings.P95Milliseconds:F3} ms; " +
            $"p99={timings.P99Milliseconds:F3} ms; max={timings.MaximumMilliseconds:F3} ms");
        _output.WriteLine(
            $"{scenario}: steady resources before/after: " +
            $"GDI={steadyBaseline.GdiObjects}/{steadyAfter.GdiObjects}; " +
            $"USER={steadyBaseline.UserObjects}/{steadyAfter.UserObjects}; " +
            $"handles={steadyBaseline.ProcessHandles}/{steadyAfter.ProcessHandles}; " +
            $"private={FormatMebibytes(steadyBaseline.PrivateBytes)}/{FormatMebibytes(steadyAfter.PrivateBytes)} MiB");
        _output.WriteLine(
            $"{scenario}: process resources before/after overlay cleanup: " +
            $"GDI={processBaseline.GdiObjects}/{processAfterCleanup.GdiObjects}; " +
            $"USER={processBaseline.UserObjects}/{processAfterCleanup.UserObjects}; " +
            $"handles={processBaseline.ProcessHandles}/{processAfterCleanup.ProcessHandles}; " +
            $"private={FormatMebibytes(processBaseline.PrivateBytes)}/{FormatMebibytes(processAfterCleanup.PrivateBytes)} MiB");

        Assert.True(
            timings.MeanMilliseconds <= maximumMeanMilliseconds,
            $"{scenario} mean submit time {timings.MeanMilliseconds:F3} ms exceeded {maximumMeanMilliseconds:F1} ms.");
        Assert.True(
            timings.P95Milliseconds <= maximumP95Milliseconds,
            $"{scenario} p95 submit time {timings.P95Milliseconds:F3} ms exceeded {maximumP95Milliseconds:F1} ms.");

        AssertResourceGrowth(
            scenario,
            "steady-state",
            steadyBaseline,
            steadyAfter,
            maximumGdiGrowth: 2,
            maximumUserGrowth: 2,
            maximumPrivateByteGrowth: 16 * 1024 * 1024);
        AssertResourceGrowth(
            scenario,
            "after cleanup",
            processBaseline,
            processAfterCleanup,
            maximumGdiGrowth: 2,
            maximumUserGrowth: 2,
            maximumPrivateByteGrowth: 24 * 1024 * 1024);
    }

    private static void AssertResourceGrowth(
        string scenario,
        string phase,
        ResourceSnapshot before,
        ResourceSnapshot after,
        int maximumGdiGrowth,
        int maximumUserGrowth,
        long maximumPrivateByteGrowth)
    {
        Assert.True(
            after.GdiObjects <= before.GdiObjects + maximumGdiGrowth,
            $"{scenario} {phase} GDI objects grew from {before.GdiObjects} to {after.GdiObjects}.");
        Assert.True(
            after.UserObjects <= before.UserObjects + maximumUserGrowth,
            $"{scenario} {phase} USER objects grew from {before.UserObjects} to {after.UserObjects}.");

        // LayeredCursorOverlay owns HWND/HDC/HBITMAP resources, represented by
        // the USER and GDI counters above. Process.HandleCount also includes
        // testhost/xUnit thread and event handles that are initialized on
        // background timers while this test runs. Its value is logged for
        // diagnostics, but it cannot be attributed to the overlay in-process.
        Assert.True(
            after.PrivateBytes <= before.PrivateBytes + maximumPrivateByteGrowth,
            $"{scenario} {phase} private memory grew from {FormatMebibytes(before.PrivateBytes)} to " +
            $"{FormatMebibytes(after.PrivateBytes)} MiB.");
    }

    private static void ForceFullCollection()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    private static ResourceSnapshot CaptureSettledResources(
        Process process,
        TimeSpan minimumObservation)
    {
        Stopwatch observation = Stopwatch.StartNew();
        ResourceSnapshot latest = ResourceSnapshot.Capture(process);
        int stableSamples = 1;
        TimeSpan deadline = minimumObservation + ResourceSettleTimeout;

        while (observation.Elapsed < minimumObservation ||
               (stableSamples < RequiredStableResourceSamples && observation.Elapsed < deadline))
        {
            Thread.Sleep(ResourceSampleInterval);
            ResourceSnapshot current = ResourceSnapshot.Capture(process);
            stableSamples = current.HasSameOverlayResourceCounts(latest)
                ? stableSamples + 1
                : 1;
            latest = current;
        }

        return latest;
    }

    private static string FormatMebibytes(long bytes) => (bytes / (1024d * 1024d)).ToString("F1");

    private readonly record struct ResourceSnapshot(
        uint GdiObjects,
        uint UserObjects,
        int ProcessHandles,
        long PrivateBytes)
    {
        public bool HasSameOverlayResourceCounts(ResourceSnapshot other) =>
            GdiObjects == other.GdiObjects &&
            UserObjects == other.UserObjects;

        public static ResourceSnapshot Capture(Process process)
        {
            process.Refresh();
            uint gdiObjects = GetGuiResources(process.Handle, GrGdiObjects);
            uint userObjects = GetGuiResources(process.Handle, GrUserObjects);
            if (gdiObjects == 0 || userObjects == 0)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "GetGuiResources could not capture the test process resource counts.");
            }

            return new ResourceSnapshot(
                gdiObjects,
                userObjects,
                process.HandleCount,
                process.PrivateMemorySize64);
        }
    }

    private readonly record struct TimingDistribution(
        double MeanMilliseconds,
        double P50Milliseconds,
        double P95Milliseconds,
        double P99Milliseconds,
        double MaximumMilliseconds)
    {
        public static TimingDistribution From(double[] elapsedMilliseconds)
        {
            double[] sorted = (double[])elapsedMilliseconds.Clone();
            Array.Sort(sorted);
            return new TimingDistribution(
                elapsedMilliseconds.Average(),
                Percentile(sorted, 0.50),
                Percentile(sorted, 0.95),
                Percentile(sorted, 0.99),
                sorted[^1]);
        }

        private static double Percentile(double[] sorted, double percentile)
        {
            int index = Math.Clamp((int)Math.Ceiling(sorted.Length * percentile) - 1, 0, sorted.Length - 1);
            return sorted[index];
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetGuiResources(nint process, int flags);
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class OverlaySoakCollection
{
    public const string Name = "Overlay soak tests";
}
