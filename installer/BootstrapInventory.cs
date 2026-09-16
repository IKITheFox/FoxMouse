using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace FoxMouse.Bootstrap
{
    public static class BootstrapInventory
    {
        public static bool HasDesktopRuntime(string minimum)
        {
            if (!Environment.Is64BitOperatingSystem) return false;
            HashSet<string> roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet"));
            using (RegistryKey machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var key = machine.OpenSubKey(@"SOFTWARE\dotnet\Setup\InstalledVersions\x64"))
            {
                if (key != null)
                {
                    var registered = key.GetValue("InstallLocation") as string;
                    if (!String.IsNullOrWhiteSpace(registered)) roots.Add(registered);
                }
            }
            foreach (string root in roots)
            {
                if (!File.Exists(Path.Combine(root, "dotnet.exe"))) continue;
                string desktop = Path.Combine(root, "shared", "Microsoft.WindowsDesktop.App");
                string runtime = Path.Combine(root, "shared", "Microsoft.NETCore.App");
                if (!Directory.Exists(desktop) || !Directory.Exists(runtime)) continue;
                foreach (string directory in Directory.GetDirectories(desktop))
                {
                    string version = Path.GetFileName(directory);
                    if (BootstrapDependencyPolicy.IsCompatibleRuntime(version, minimum) &&
                        File.Exists(Path.Combine(directory, "System.Windows.Forms.dll")) &&
                        File.Exists(Path.Combine(runtime, version, "coreclr.dll"))) return true;
                }
            }
            return false;
        }

        public static bool PackageMatches(string fullName, string family, Version minimum, string architecture)
        {
            string[] parts = fullName.Split('_');
            if (parts.Length != 5 || parts[0] + "_" + parts[4] != family ||
                !String.Equals(parts[2], architecture, StringComparison.OrdinalIgnoreCase)) return false;
            try { return Version.Parse(parts[1]) >= minimum; }
            catch (ArgumentException) { return false; }
            catch (FormatException) { return false; }
            catch (OverflowException) { return false; }
        }

        // A registered package is not proof of deployment health. The bootstrap
        // runner must also perform the application's startup probe before launch.
        public static bool HasRegisteredPackage(string family, Version minimum, string architecture)
        {
            uint count = 0, characters = 0;
            int status = GetPackagesByPackageFamily(family, ref count, IntPtr.Zero, ref characters, IntPtr.Zero);
            if (status != 122 || count == 0 || count > 10000 || characters > 8 * 1024 * 1024) return false;
            IntPtr names = Marshal.AllocHGlobal(checked((int)count * IntPtr.Size));
            IntPtr buffer = Marshal.AllocHGlobal(checked((int)characters * 2));
            try
            {
                status = GetPackagesByPackageFamily(family, ref count, names, ref characters, buffer);
                if (status != 0) return false; // Inventory changed; let the next detection retry.
                for (int index = 0; index < count; index++)
                {
                    var fullName = Marshal.PtrToStringUni(Marshal.ReadIntPtr(names, index * IntPtr.Size));
                    if (fullName != null && PackageMatches(fullName, family, minimum, architecture)) return true;
                }
                return false;
            }
            finally { Marshal.FreeHGlobal(buffer); Marshal.FreeHGlobal(names); }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetPackagesByPackageFamily(string packageFamilyName,
            ref uint count, IntPtr packageFullNames, ref uint bufferLength, IntPtr buffer);
    }
}
