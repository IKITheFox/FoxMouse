#if NET5_0_OR_GREATER
// This source also runs under the modern test host; production uses inbox
// .NET Framework and C# 5, where nullable annotations are unavailable.
#pragma warning disable CS8600, CS8622, CS8625, SYSLIB0014
#endif
using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace FoxMouse.Bootstrap
{
    internal static class BootstrapProgram
    {
        [STAThread]
#if NET5_0_OR_GREATER
        private static int BootstrapMain(string[] args)
#else
        private static int Main(string[] args)
#endif
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (args.Contains("--probe"))
                return BootstrapInventory.HasDesktopRuntime("10.0.11") && HasAppRuntime() ? 0 : 2;
            using (BootstrapWindow window = new BootstrapWindow())
            {
                if (args.Contains("--smoke-test"))
                    window.Shown += async delegate { await Task.Delay(300); window.Close(); };
                Application.Run(window);
                return window.ExitCode;
            }
        }

        internal static bool HasAppRuntime()
        {
            return BootstrapInventory.HasRegisteredPackage("Microsoft.WindowsAppRuntime.2_8wekyb3d8bbwe", new Version(2, 4, 0, 0), "x64") &&
                BootstrapInventory.HasRegisteredPackage("MicrosoftCorporationII.WinAppRuntime.Main.2_8wekyb3d8bbwe", new Version(2, 4, 0, 0), "x64") &&
                BootstrapInventory.HasRegisteredPackage("MicrosoftCorporationII.WinAppRuntime.Singleton_8wekyb3d8bbwe", new Version(8002, 4, 0, 0), "x64");
        }
    }

    internal sealed class BootstrapWindow : Form
    {
        private readonly ComboBox language = new FoxMouse.Presentation.ThemeAwareComboBox();
        private readonly Label heading = new Label();
        private readonly Label description = new Label();
        private readonly Label status = new Label();
        private readonly ProgressBar downloadProgress = new FoxMouse.Presentation.InstallerProgressBar();
        private readonly Button openLog = new FoxMouse.Presentation.InstallerButton();
        private readonly Label locationTitle = new Label();
        private readonly Label location = new Label();
        private readonly Button browse = new FoxMouse.Presentation.InstallerButton();
        private readonly TableLayoutPanel locationPanel = new TableLayoutPanel();
        private string installParent;
        private bool existingInstallation;
        private readonly string logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FoxMouse", "BootstrapLogs", "setup-" + Guid.NewGuid().ToString("N") + ".log");
        private string phase = "Startup";
        private readonly Button next = new FoxMouse.Presentation.InstallerButton();
        private readonly Button close = new FoxMouse.Presentation.InstallerButton();
        private readonly CancellationTokenSource downloadCancellation = new CancellationTokenSource();
        private bool running;
        private bool closingRequested;
        private bool restartRequired;
        internal int ExitCode;
        private bool English { get { return language.SelectedIndex == 1; } }
        private string TextFor(string chinese, string english) { return English ? english : chinese; }

        internal BootstrapWindow()
        {
            SuspendLayout();
            Text = "FoxMouse Setup";
            Font = new Font("Segoe UI", 10F);
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96, 96);
            ClientSize = new Size(680, 520);
            MinimumSize = SizeFromClientSize(new Size(600, 440));
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            using (Icon icon = Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location))
                if (icon != null) Icon = (Icon)icon.Clone();
            TableLayoutPanel layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(32), ColumnCount = 1, RowCount = 6 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            heading.AutoSize = true;
            heading.Font = new Font(Font.FontFamily, 22, FontStyle.Bold);
            heading.Margin = new Padding(0, 0, 0, 16);
            description.AutoSize = true;
            description.Dock = DockStyle.Top;
            description.Margin = new Padding(0, 0, 0, 16);
            language.Name = "LanguageChoice";
            language.AccessibleName = "Language / 语言";
            language.DropDownStyle = ComboBoxStyle.DropDownList;
            language.Items.AddRange(new object[] { "简体中文", "English" });
            language.Width = 180;
            language.MinimumSize = new Size(180, 0);
            language.Anchor = AnchorStyles.Left;
            language.Margin = new Padding(0, 0, 0, 8);
            language.SelectedIndex = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh" ? 0 : 1;
            installParent = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");
            string installRoot = Path.Combine(installParent, "FoxMouse");
            // Advisory only: the deployment engine revalidates registration and
            // ownership before any product files are changed.
            using (var registration = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\FoxMouse"))
            {
                string registered = registration == null ? null : registration.GetValue("InstallLocation") as string;
                existingInstallation = registration != null || File.Exists(Path.Combine(installRoot, "FoxMouse.exe"));
                if (!string.IsNullOrWhiteSpace(registered)) installRoot = registered;
            }
            locationPanel.Dock = DockStyle.Top;
            locationPanel.AutoSize = true;
            locationPanel.ColumnCount = 2;
            locationPanel.Padding = new Padding(0, 16, 0, 16);
            locationPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            locationPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            locationTitle.AutoSize = true;
            location.AutoEllipsis = true;
            location.Dock = DockStyle.Fill;
            location.Height = 32;
            location.Text = installRoot;
            location.AccessibleName = "Installation folder / 安装位置";
            browse.AutoSize = true;
            browse.Enabled = !existingInstallation;
            browse.MinimumSize = new Size(90, 32);
            locationPanel.Controls.Add(locationTitle, 0, 0);
            locationPanel.Controls.Add(browse, 1, 0);
            locationPanel.Controls.Add(location, 0, 1);
            locationPanel.SetColumnSpan(location, 2);
            browse.Click += delegate
            {
                using (FolderBrowserDialog picker = new FolderBrowserDialog { SelectedPath = installParent, Description = TextFor("选择安装文件夹，自动添加 FoxMouse。", "Choose a folder. FoxMouse is appended automatically.") })
                {
                    if (picker.ShowDialog(this) != DialogResult.OK) return;
                    installParent = NormalizeInstallParent(picker.SelectedPath);
                    location.Text = Path.Combine(installParent, "FoxMouse");
                }
            };
            status.Dock = DockStyle.Top;
            status.Height = 100;
            status.Margin = new Padding(0, 14, 0, 14);
            FlowLayoutPanel actions = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill };
            next.AutoSize = close.AutoSize = true;
            next.MinimumSize = close.MinimumSize = openLog.MinimumSize = new Size(108, 40);
            next.Margin = close.Margin = openLog.Margin = new Padding(12, 0, 0, 0);
            openLog.AutoSize = true;
            openLog.Visible = false;
            openLog.Click += delegate { if (File.Exists(logPath)) Process.Start(new ProcessStartInfo("notepad.exe", "\"" + logPath + "\"") { UseShellExecute = true }); };
            actions.Controls.Add(close); actions.Controls.Add(next); actions.Controls.Add(openLog);
            Panel progressPanel = new Panel { Dock = DockStyle.Fill };
            progressPanel.SizeChanged += delegate { status.Height = Math.Max(1, Math.Min(100, progressPanel.ClientSize.Height - downloadProgress.Height)); };
            downloadProgress.Dock = DockStyle.Top;
            downloadProgress.Height = 8;
            downloadProgress.Visible = false;
            progressPanel.Controls.Add(downloadProgress);
            progressPanel.Controls.Add(status);
            layout.Controls.Add(heading, 0, 0); layout.Controls.Add(description, 0, 1);
            layout.Controls.Add(language, 0, 2); layout.Controls.Add(locationPanel, 0, 3); layout.Controls.Add(progressPanel, 0, 4); layout.Controls.Add(actions, 0, 5);
            Controls.Add(layout);
            language.SelectedIndexChanged += delegate { ApplyLanguage(); };
            next.Click += async delegate { await ContinueAsync(); };
            close.Click += delegate { Close(); };
            FormClosing += delegate(object sender, FormClosingEventArgs e)
            {
                if (running)
                {
                    e.Cancel = true;
                    closingRequested = true;
                    downloadCancellation.Cancel();
                    status.Text = TextFor("正在取消下载；若组件安装已开始，将在完成后关闭。", "Cancelling download; an installer already running will be allowed to finish.");
                }
            };
            // Bound wrapping to the actual cell, including after initial layout.
            // An implicit AutoSize column can grow to the unwrapped description
            // and push the right-aligned actions outside the visible client area.
            layout.SizeChanged += delegate { description.MaximumSize = new Size(Math.Max(1, layout.ClientSize.Width - layout.Padding.Horizontal), 0); };
            ApplyTheme();
            ApplyLanguage();
            ResumeLayout(true);
        }

        private void ApplyTheme()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
            {
                bool dark = key != null && Convert.ToInt32(key.GetValue("AppsUseLightTheme", 1)) == 0 && !SystemInformation.HighContrast;
                BackColor = dark ? Color.FromArgb(32, 32, 32) : SystemColors.Window;
                ForeColor = dark ? Color.WhiteSmoke : SystemColors.WindowText;
                language.BackColor = dark ? Color.FromArgb(44, 44, 44) : Color.FromArgb(239, 240, 242);
                language.ForeColor = ForeColor;
                downloadProgress.BackColor = language.BackColor;
                foreach (Button button in new Button[] { next, close, browse, openLog })
                {
                    button.BackColor = SystemInformation.HighContrast ? SystemColors.Control : language.BackColor;
                    button.ForeColor = ForeColor;
                }
                next.BackColor = SystemInformation.HighContrast ? SystemColors.Highlight : Color.FromArgb(246, 107, 43);
                next.ForeColor = SystemInformation.HighContrast ? SystemColors.HighlightText : Color.Black;
                if (IsHandleCreated) FoxMouse.Presentation.InstallerChrome.Apply(Handle, dark, BackColor, ForeColor);
            }
            Invalidate(true);
        }

        protected override void WndProc(ref Message message)
        {
            base.WndProc(ref message);
            if ((message.Msg == 0x031A || message.Msg == 0x001A || message.Msg == 0x0015) &&
                IsHandleCreated && !IsDisposed && !Disposing)
                BeginInvoke(new Action(delegate { if (!IsDisposed && !Disposing) ApplyTheme(); }));
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyTheme();
        }

        private void ApplyLanguage()
        {
            Text = TextFor("FoxMouse 在线安装程序", "FoxMouse Online Setup");
            heading.Text = TextFor("安装 FoxMouse", "Install FoxMouse");
            var release = (System.Reflection.AssemblyInformationalVersionAttribute)Attribute.GetCustomAttribute(typeof(BootstrapWindow).Assembly, typeof(System.Reflection.AssemblyInformationalVersionAttribute));
            Text += " " + (release == null ? "" : release.InformationalVersion.Replace("-beta", " Beta"));
            description.Text = TextFor("选择安装位置，继续后自动补齐组件。", "Choose a folder. Continue to install missing components.");
            locationTitle.Text = TextFor("安装位置", "Installation folder");
            browse.Text = TextFor("更改…", "Change…");
            next.Text = TextFor("继续", "Continue"); close.Text = TextFor("关闭", "Close");
            openLog.Text = TextFor("打开日志", "Open log");
            status.Text = existingInstallation ? TextFor("已安装，将使用原目录。", "Existing installation: original folder retained.") : TextFor("就绪", "Ready");
            if (restartRequired) ShowRestartRequired();
        }

        private void ShowRestartRequired()
        {
            ShowStage(TextFor("需要重启 Windows", "Restart Windows"), TextFor("运行组件已安装，重启后才能继续。", "Restart Windows to continue after installing components."));
            status.Text = TextFor("组件安装需要重启 Windows。请关闭此窗口，重启电脑后重新运行 FoxMouse 安装程序。", "The component requires Windows to restart. Close this window, restart your computer, then run FoxMouse Setup again.");
            next.Enabled = false;
            next.Visible = false;
            close.Text = TextFor("确定", "OK");
        }

        private void ShowStage(string title, string hint)
        {
            heading.Text = title;
            description.Text = hint;
            language.Visible = false;
            locationPanel.Visible = false;
            next.Visible = false;
        }

        private async Task ContinueAsync()
        {
            if (running || restartRequired) return;
            running = true; next.Enabled = language.Enabled = browse.Enabled = false;
            openLog.Visible = false;
            phase = TextFor("准备安装", "Preparing setup");
            ShowStage(TextFor("正在准备", "Preparing setup"), TextFor("正在检查所需组件。", "Checking required components."));
            string root = Path.Combine(Path.GetTempPath(), "FoxMouse.Online." + Guid.NewGuid().ToString("N"));
            try
            {
                if (!Environment.Is64BitOperatingSystem) throw new PlatformNotSupportedException("FoxMouse requires 64-bit Windows.");
                // Do not install dependencies for an incomplete developer build.
                using (Stream bundle = Assembly.GetExecutingAssembly().GetManifestResourceStream("FoxMouse.SetupBundle.zip"))
                {
                    if (bundle == null) throw new InvalidDataException("This build has no embedded setup bundle.");
                    Directory.CreateDirectory(root);
                    BootstrapBundle.Extract(bundle, root);
                }
                string setup = Path.Combine(root, "FoxMouse-Setup-x64.exe");
                string package = Path.Combine(root, "FoxMouse-package.zip");
                if (!File.Exists(setup) || !File.Exists(package)) throw new FileNotFoundException("Setup bundle is incomplete.");
                if (!BootstrapInventory.HasDesktopRuntime("10.0.11"))
                    await InstallDependencyAsync(root, "dotnet.exe", new Uri("https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/10.0.11/windowsdesktop-runtime-10.0.11-win-x64.exe"),
                        "4dbf26b0b78f55c5f59a46c3c81327b23a04f449f7ac6798204dcd19d99459258936daaede61d1b8c1ba523d6c26bf68bac86b3371d22e67cef235edbdc2f26c",
                        "/install /quiet /norestart", delegate { return BootstrapInventory.HasDesktopRuntime("10.0.11"); });
                if (closingRequested) return;
                if (!BootstrapProgram.HasAppRuntime())
                    await InstallDependencyAsync(root, "windowsappruntime.exe", new Uri("https://aka.ms/windowsappsdk/2.4/2.4.0/windowsappruntimeinstall-x64.exe"),
                        "AB866DA27FB6B1696C5399881F6F741BF963FF0D6DF6A8009BD2FE13AC604CADDD4FEB253CD86690E7CCDA61337880B3C911273F9A5324386B33AA512AB1363E",
                        "--quiet", BootstrapProgram.HasAppRuntime);
                if (closingRequested) return;
                status.Text = TextFor("正在打开安装界面…", "Opening Setup…");
                Hide();
                ExitCode = await RunProcessAsync(setup, BuildSetupArguments(package, English, existingInstallation ? "" : installParent), false);
                if (ExitCode != 0) throw new InvalidOperationException("FoxMouse Setup exited with code " + ExitCode + ".");
                closingRequested = true;
            }
            catch (Exception exception)
            {
                BootstrapRestartRequiredException restart = exception as BootstrapRestartRequiredException;
                if (restart != null)
                {
                    restartRequired = true;
                    ExitCode = restart.ExitCode;
                    Show();
                    ShowRestartRequired();
                }
                else if (exception is OperationCanceledException && closingRequested) ExitCode = 0;
                else
                {
                    ExitCode = 1;
                    Show();
                    try
                    {
                        Directory.CreateDirectory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FoxMouse", "BootstrapLogs"));
                        File.AppendAllText(logPath, DateTime.UtcNow.ToString("O") + " | " + phase + Environment.NewLine + exception + Environment.NewLine);
                        openLog.Visible = true;
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                    status.Text = TextFor("未完成：", "Not completed: ") + phase + Environment.NewLine +
                        exception.Message + Environment.NewLine + "0x" + exception.HResult.ToString("X8");
                    next.Text = TextFor("重试", "Retry");
                    ShowStage(TextFor("暂未完成", "Not completed"), TextFor("请查看错误详情后重试。", "Review the error details and try again."));
                    next.Visible = true;
                    close.Text = TextFor("关闭", "Close");
                }
            }
            finally
            {
                running = false; next.Enabled = !restartRequired; language.Enabled = true;
                browse.Enabled = !existingInstallation && !restartRequired;
                downloadProgress.Visible = false;
                try { BootstrapBundle.DeleteOwnedRoot(root); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                if (closingRequested) Close();
            }
        }

        private async Task InstallDependencyAsync(string root, string fileName, Uri uri, string hash, string arguments, Func<bool> detect)
        {
            string file = Path.Combine(root, fileName);
            string component = fileName == "dotnet.exe" ? ".NET Desktop Runtime" : "Windows App Runtime";
            phase = TextFor("下载 ", "Downloading ") + component;
            ShowStage(TextFor("下载所需组件", "Downloading components"), TextFor("正在从微软下载运行组件。", "Downloading runtime components from Microsoft."));
            close.Text = TextFor("取消", "Cancel");
            status.Text = phase;
            downloadProgress.Style = ProgressBarStyle.Marquee;
            downloadProgress.Visible = true;
            bool downloading = true;
            Stopwatch elapsed = Stopwatch.StartNew();
            long lastUpdate = -200;
            using (BootstrapDownloader downloader = new BootstrapDownloader())
            {
                Progress<long> progress = new Progress<long>(delegate(long bytes)
                {
                    if (!downloading || closingRequested || IsDisposed || elapsed.ElapsedMilliseconds - lastUpdate < 100) return;
                    lastUpdate = elapsed.ElapsedMilliseconds;
                    long? total = downloader.ExpectedLength;
                    string amount = (bytes / 1048576d).ToString("F1") + " MiB";
                    if (total.HasValue && total.Value > 0)
                    {
                        int percent = (int)Math.Min(100, bytes * 100d / total.Value);
                        downloadProgress.Style = ProgressBarStyle.Continuous;
                        downloadProgress.Value = percent;
                        amount += " / " + (total.Value / 1048576d).ToString("F1") + " MiB (" + percent + "%)";
                    }
                    status.Text = phase + Environment.NewLine + amount + " · " +
                        (bytes / 1048576d / Math.Max(0.1, elapsed.Elapsed.TotalSeconds)).ToString("F1") + " MiB/s";
                });
                try
                {
                    using (FileStream stream = new FileStream(file, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read))
                    {
                        await downloader.DownloadAsync(uri, hash, 300L * 1024 * 1024, stream, progress, downloadCancellation.Token);
                        stream.Flush(true);
                    }
                }
                finally { downloading = false; }
            }
            if (closingRequested) return;
            phase = TextFor("校验 ", "Verifying ") + component;
            status.Text = phase;
            downloadProgress.Style = ProgressBarStyle.Marquee;
            int result = await BootstrapDownloader.RunVerifiedAsync(file, hash, async delegate(string verifiedFile)
            {
                // Verification runs off the UI thread; marshal stage display only.
                if (!IsDisposed) BeginInvoke(new Action(delegate
                {
                    phase = TextFor("安装 ", "Installing ") + component;
                    ShowStage(TextFor("安装所需组件", "Installing components"), TextFor("组件安装完成后将自动继续。", "Setup will continue when components are installed."));
                    close.Text = TextFor("关闭", "Close");
                    if (!closingRequested) status.Text = phase;
                }));
                return await RunProcessAsync(verifiedFile, arguments, true);
            }, downloadCancellation.Token);
            phase = TextFor("检测 ", "Checking ") + component;
            status.Text = phase;
            BootstrapDependencyPolicy.VerifyInstallationResult(result, result == 0 && detect());
        }

        internal static string NormalizeInstallParent(string selected)
        {
            string path = Path.GetFullPath(selected).TrimEnd(Path.DirectorySeparatorChar);
            if (string.Equals(Path.GetFileName(path), "FoxMouse", StringComparison.OrdinalIgnoreCase))
                path = Path.GetDirectoryName(path);
            // Restore drive-root separator before quoting the command line.
            return Path.GetFullPath(path + Path.DirectorySeparatorChar);
        }

        internal static string BuildSetupArguments(string package, bool english, string parent)
        {
            // Use a trailing dot so a drive-root trailing slash cannot escape
            // the closing quote in Windows command-line parsing.
            return "--package \"" + package + "\" --language " + (english ? "en-US" : "zh-CN") +
                (string.IsNullOrEmpty(parent) ? "" : " --install-parent \"" + Path.Combine(parent, ".") + "\"");
        }

        internal static ProcessStartInfo CreateStartInfo(string executable, string arguments, bool dependency)
        {
            return new ProcessStartInfo(executable, arguments) {
                UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(executable),
                WindowStyle = dependency ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal
            };
        }

        private static Task<int> RunProcessAsync(string executable, string arguments, bool dependency)
        {
            return Task.Run(delegate
            {
                using (Process process = Process.Start(CreateStartInfo(executable, arguments, dependency)))
                {
                    if (process == null) throw new InvalidOperationException("Unable to start installer.");
                    process.WaitForExit(); return process.ExitCode;
                }
            });
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) downloadCancellation.Dispose();
            base.Dispose(disposing);
        }
    }
}
