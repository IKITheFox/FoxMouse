// Kept compatible with the Windows-inbox .NET Framework compiler. The online
// bootstrapper must not require the .NET runtime that it is about to install.
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace FoxMouse.Bootstrap
{
    public sealed class BootstrapRestartRequiredException : InvalidOperationException
    {
        public int ExitCode { get; private set; }

        public BootstrapRestartRequiredException(int exitCode)
            : base("Windows must restart before dependency installation can continue.")
        {
            ExitCode = exitCode;
        }
    }

    public static class BootstrapDependencyPolicy
    {
        private static readonly HashSet<string> Hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "builds.dotnet.microsoft.com", "download.visualstudio.microsoft.com",
            "download.microsoft.com", "aka.ms"
        };

        public static void ValidateDownloadUri(Uri uri)
        {
            if (uri == null || !uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps ||
                uri.Port != 443 || !String.IsNullOrEmpty(uri.UserInfo) ||
                !String.IsNullOrEmpty(uri.Fragment) || !Hosts.Contains(uri.DnsSafeHost))
                throw new InvalidDataException("Dependency download URL is not an approved Microsoft HTTPS origin.");
        }

        public static bool IsCompatibleRuntime(string installed, string minimum)
        {
            // Previews do not satisfy stable prerequisites. Major/minor must
            // match the application's runtimeconfig; do not assume roll-forward.
            try
            {
                Version actual = Version.Parse(installed);
                Version required = Version.Parse(minimum);
                return actual.Major == required.Major && actual.Minor == required.Minor && actual >= required;
            }
            catch (ArgumentException) { return false; }
            catch (FormatException) { return false; }
            catch (OverflowException) { return false; }
        }

        public static void VerifySha512(Stream payload, string expected)
        {
            if (expected == null || expected.Length != 128)
                throw new InvalidDataException("Dependency manifest has no valid SHA-512 digest.");
            byte[] reference = new byte[64];
            try
            {
                for (int i = 0; i < reference.Length; i++)
                    reference[i] = Convert.ToByte(expected.Substring(i * 2, 2), 16);
            }
            catch (Exception exception)
            {
                if (!(exception is FormatException) && !(exception is OverflowException)) throw;
                throw new InvalidDataException("Dependency SHA-512 digest is malformed.", exception);
            }
            using (SHA512 algorithm = SHA512.Create())
            {
                byte[] actual = algorithm.ComputeHash(payload);
                int mismatch = 0;
                for (int i = 0; i < reference.Length; i++) mismatch |= actual[i] ^ reference[i];
                if (mismatch != 0) throw new InvalidDataException("Dependency checksum mismatch. The file must not be executed.");
            }
        }

        public static bool RequiresRestart(int exitCode) { return exitCode == 3010 || exitCode == 1641; }

        public static void VerifyInstallationResult(int exitCode, bool detectedAfterInstall)
        {
            if (RequiresRestart(exitCode))
                throw new BootstrapRestartRequiredException(exitCode);
            if (exitCode != 0)
                throw new InvalidOperationException("Dependency installer failed with exit code " + exitCode + ".");
            if (!detectedAfterInstall)
                throw new InvalidOperationException("Dependency is still unavailable after installation.");
        }
    }
}
