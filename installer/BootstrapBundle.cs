using System;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace FoxMouse.Bootstrap
{
    public static class BootstrapBundle
    {
        private static void ValidateOwnedRoot(string root)
        {
            string full = Path.GetFullPath(root);
            if (!String.Equals(Path.GetDirectoryName(full), Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(full).StartsWith("FoxMouse.Online.", StringComparison.Ordinal) ||
                Path.GetFileName(full).Length != "FoxMouse.Online.".Length + 32)
                throw new InvalidDataException("Invalid bootstrap staging root.");
            if (Directory.Exists(full) && (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Bootstrap staging root cannot be a link.");
        }

        public static void Extract(Stream source, string root)
        {
            ValidateOwnedRoot(root);
            if (Directory.EnumerateFileSystemEntries(root).Any()) throw new InvalidDataException("Bootstrap staging root must be empty.");
            string prefix = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
            using (ZipArchive archive = new ZipArchive(source, ZipArchiveMode.Read, true))
            {
                long total = 0;
                if (archive.Entries.Count > 10000) throw new InvalidDataException("Too many setup bundle entries.");
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    if (entry.FullName.Contains(":") || entry.FullName.Contains("\\") || entry.FullName.Split('/').Any(part => part == ".." || part.EndsWith(".") || part.EndsWith(" ")))
                        throw new InvalidDataException("Unsafe setup bundle path.");
                    string target = Path.GetFullPath(Path.Combine(root, entry.FullName));
                    if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Setup entry escapes staging root.");
                    total = checked(total + entry.Length);
                    if (total > 1024L * 1024 * 1024) throw new InvalidDataException("Setup bundle is too large.");
                    if (entry.FullName.EndsWith("/")) { Directory.CreateDirectory(target); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(target) ?? root);
                    using (Stream input = entry.Open())
                    using (FileStream output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        byte[] bytes = new byte[64 * 1024];
                        long written = 0;
                        int read;
                        while ((read = input.Read(bytes, 0, bytes.Length)) > 0)
                        {
                            written += read;
                            if (written > entry.Length) throw new InvalidDataException("Setup entry exceeds its declared size.");
                            output.Write(bytes, 0, read);
                        }
                        if (written != entry.Length) throw new InvalidDataException("Setup entry is incomplete.");
                    }
                }
            }
        }

        public static void DeleteOwnedRoot(string root)
        {
            ValidateOwnedRoot(root);
            if (!Directory.Exists(root)) return;
            EnsureNoLinks(root);
            Directory.Delete(root, true);
        }

        private static void EnsureNoLinks(string root)
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(root))
            {
                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Refusing to clean a linked staging entry.");
                if ((attributes & FileAttributes.Directory) != 0) EnsureNoLinks(entry);
            }
        }
    }
}
