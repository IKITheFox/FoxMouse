using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Diagnostics;
using FoxMouse.Platform.Windows.Rendering;

namespace FoxMouse.Platform.Windows.Tests;

[CollectionDefinition("Locator desktop composition", DisableParallelization = true)]
public sealed class LocatorDesktopCompositionCollection;

[Collection("Locator desktop composition")]
public sealed class LocatorDesktopCompositeTests
{
    [WindowsDesktopFact]
    public void ActualLayeredRingHasNoWhiteOutlineOnOwnedDesktopBackgrounds()
    {
        // WinForms caches process-wide display initialization. Run the same
        // assertions in a fresh host, rather than depending on test ordering.
        const string childMarker = "FOXMOUSE_LOCATOR_COMPOSITE_CHILD";
        if (Environment.GetEnvironmentVariable(childMarker) != "1")
        {
            string dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
            Assert.True(File.Exists(dotnet), "A dotnet host is required for isolated desktop validation.");
            ProcessStartInfo start = new(dotnet)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            start.ArgumentList.Add("vstest");
            start.ArgumentList.Add(typeof(LocatorDesktopCompositeTests).Assembly.Location);
            start.ArgumentList.Add("--TestCaseFilter:FullyQualifiedName=FoxMouse.Platform.Windows.Tests.LocatorDesktopCompositeTests.ActualLayeredRingHasNoWhiteOutlineOnOwnedDesktopBackgrounds");
            start.ArgumentList.Add("--logger:console;verbosity=detailed");
            start.Environment[childMarker] = "1";
            using Process child = Process.Start(start) ?? throw new InvalidOperationException("Cannot launch isolated visual test.");
            Task<string> output = child.StandardOutput.ReadToEndAsync();
            Task<string> error = child.StandardError.ReadToEndAsync();
            if (!child.WaitForExit(45_000))
            {
                child.Kill(entireProcessTree: true);
                throw new TimeoutException("Isolated locator visual test exceeded its 45-second watchdog.");
            }
            string transcript = output.GetAwaiter().GetResult() + error.GetAwaiter().GetResult();
            Console.WriteLine(transcript);
            Assert.True(child.ExitCode == 0, transcript);
            Assert.Contains("FoxMouse locator composite assertions completed.", transcript);
            return;
        }
        StaTestThread.Run(() =>
        {
            nint previousDpi = SetThreadDpiAwarenessContext(new nint(-4));
            Assert.NotEqual(nint.Zero, previousDpi);
            try
            {
                using Form background = new()
                {
                    Text = "FoxMouse locator composition test",
                    AutoScaleMode = AutoScaleMode.None,
                    ClientSize = new Size(640, 500),
                    StartPosition = FormStartPosition.CenterScreen,
                    FormBorderStyle = FormBorderStyle.FixedToolWindow,
                    TopMost = true,
                };
                background.Show();
                SetThreadDpiAwarenessContext(new nint(-4));
                using LayeredCursorOverlay overlay = new();
                foreach (int shade in new[] { 24, 230 })
                {
                    Color surface = Color.FromArgb(shade, shade, shade);
                    background.BackColor = surface;
                    background.Refresh();
                    Assert.True(GetWindowRect(background.Handle, out WindowRectangle hostRectangle));
                    Rectangle hostBounds = hostRectangle.Bounds;
                    Point center = new(hostBounds.Left + hostBounds.Width / 2, hostBounds.Top + hostBounds.Height / 2);
                    overlay.ShowLocator(center, 1d);
                    Assert.True(GetWindowRect(overlay.Handle, out WindowRectangle overlayRectangle));
                    Rectangle capture = overlayRectangle.Bounds;
                    overlay.HideOverlay();
                    Assert.True(Rectangle.Inflate(hostBounds, -48, -48).Contains(capture),
                        "Capture must stay entirely inside our opaque test background.");
                    Settle();
                    bool backgroundReady = false;
                    for (int attempt = 0; attempt < 20 && !backgroundReady; attempt++)
                    {
                        // Fail if another window occludes our fixture; never save
                        // a baseline containing unrelated desktop/application data.
                        using Bitmap before = Capture(capture);
                        backgroundReady = true;
                        for (int y = 0; y < before.Height && backgroundReady; y++)
                            for (int x = 0; x < before.Width; x++)
                                if (surface.ToArgb() != before.GetPixel(x, y).ToArgb())
                                {
                                    backgroundReady = false;
                                    break;
                                }
                        if (!backgroundReady) Settle();
                    }
                    Assert.True(backgroundReady, $"Owned background did not become uniformly visible: capture={capture}; host={background.Bounds}; dpi={background.DeviceDpi}");
                    overlay.ShowLocator(center, 1d);
                    Settle();
                    using Bitmap rendered = Capture(capture);
                    int ringPixels = 0;
                    for (int y = 0; y < rendered.Height; y++)
                        for (int x = 0; x < rendered.Width; x++)
                        {
                            Color pixel = rendered.GetPixel(x, y);
                            if (Math.Abs(pixel.R - shade) + Math.Abs(pixel.G - shade) + Math.Abs(pixel.B - shade) < 6)
                                continue;
                            Assert.True(pixel.R > pixel.G && pixel.G >= pixel.B,
                                $"Non-orange composited edge on {shade} at {x},{y}: {pixel}");
                            ringPixels++;
                        }
                    Assert.True(ringPixels > 100, "The actual layered window must appear in the desktop capture.");
                    Assert.Equal(surface.ToArgb(), rendered.GetPixel(rendered.Width / 2, rendered.Height / 2).ToArgb());
                    string evidenceRoot = Path.Combine(Path.GetTempPath(), "FoxMouse.LocatorCompositeTests", Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(evidenceRoot);
                    rendered.Save(Path.Combine(evidenceRoot, $"desktop-ring-{shade}.png"), ImageFormat.Png);
                    Console.WriteLine("Locator desktop evidence: " + evidenceRoot);
                    overlay.HideOverlay();
                }
            }
            finally { SetThreadDpiAwarenessContext(previousDpi); }
        });
        Console.WriteLine("FoxMouse locator composite assertions completed.");
    }

    private static void Settle()
    {
        Application.DoEvents();
        SetThreadDpiAwarenessContext(new nint(-4));
        Thread.Sleep(150);
        Application.DoEvents();
        SetThreadDpiAwarenessContext(new nint(-4));
    }

    private static Bitmap Capture(Rectangle bounds)
    {
        Bitmap image = new(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using Graphics graphics = Graphics.FromImage(image);
        nint target = graphics.GetHdc();
        nint screen = GetDC(nint.Zero);
        try
        {
            Assert.NotEqual(nint.Zero, screen);
            Assert.True(BitBlt(target, 0, 0, bounds.Width, bounds.Height, screen,
                bounds.X, bounds.Y, 0x00CC0020 | 0x40000000), "Desktop composition capture failed.");
        }
        finally
        {
            if (screen != nint.Zero) ReleaseDC(nint.Zero, screen);
            graphics.ReleaseHdc(target);
        }
        return image;
    }

    [DllImport("user32.dll")]
    private static extern nint SetThreadDpiAwarenessContext(nint context);
    [StructLayout(LayoutKind.Sequential)]
    private struct WindowRectangle
    {
        public int Left, Top, Right, Bottom;
        public readonly Rectangle Bounds => Rectangle.FromLTRB(Left, Top, Right, Bottom);
    }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out WindowRectangle rectangle);
    [DllImport("user32.dll")]
    private static extern nint GetDC(nint window);
    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(nint target, int x, int y, int width, int height,
        nint source, int sourceX, int sourceY, uint operation);
}
