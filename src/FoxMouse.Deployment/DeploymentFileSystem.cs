using System.IO.Compression;
using System.Security.Cryptography;

namespace FoxMouse.Deployment;

internal static class DeploymentFileSystem
{
    private static readonly int[] MoveRetryDelaysMilliseconds = [50, 100, 200, 400, 800, 1_000, 1_000];
    internal const int MaxArchiveEntries = 4096;
    internal const long MaxArchiveEntryBytes = 512L * 1024 * 1024;
    internal const long MaxArchiveExpandedBytes = 2L * 1024 * 1024 * 1024;

    internal static string FullPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("A filesystem path is required.", nameof(path));
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    internal static void RequireSafeLeaf(string path, string label)
    {
        string fullPath = FullPath(path);
        string? parent = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(parent) ||
            string.Equals(fullPath, Path.GetPathRoot(fullPath), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"The {label} must be a child path: {fullPath}");
        }
    }

    internal static void EnsureNoReparsePoints(string root)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        DirectoryInfo rootInfo = new(root);
        if ((rootInfo.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException($"Reparse-point directory is not allowed: {root}");
        }

        foreach (FileSystemInfo entry in rootInfo.EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException($"Reparse-point entry is not allowed: {entry.FullName}");
            }
        }
    }

    internal static void DeleteTree(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        RequireSafeLeaf(path, "delete target");
        EnsureNoReparsePoints(path);
        Directory.Delete(path, recursive: true);
    }

    internal static void MoveDirectory(string source, string destination)
    {
        RequireSafeLeaf(source, "move source");
        RequireSafeLeaf(destination, "move destination");
        EnsureNoReparsePoints(source);
        if (Directory.Exists(destination) || File.Exists(destination))
        {
            throw new IOException($"Move destination already exists: {destination}");
        }

        for (int attempt = 0; ; attempt++)
        {
            try
            {
                Directory.Move(source, destination);
                return;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException &&
                attempt < MoveRetryDelaysMilliseconds.Length &&
                Directory.Exists(source) &&
                !Directory.Exists(destination) &&
                !File.Exists(destination))
            {
                // The root launcher has already exited before a detached
                // maintenance host reaches this point. Windows Security,
                // indexing, or final image-section teardown can nevertheless
                // retain a non-delete-sharing handle for a short interval.
                // Revalidate the exact source on every bounded retry; a changed
                // or occupied target falls through immediately on the next try.
                Thread.Sleep(MoveRetryDelaysMilliseconds[attempt]);
                EnsureNoReparsePoints(source);
            }
        }
    }

    internal static string ComputeSha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    internal static string CopyFileAndComputeSha256(string sourcePath, string destinationPath)
    {
        string source = FullPath(sourcePath);
        string destination = FullPath(destinationPath);
        RequireSafeLeaf(destination, "trusted package copy");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        using FileStream input = new(
            source,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 1024,
            FileOptions.SequentialScan);
        using FileStream output = new(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 1024 * 1024,
            FileOptions.SequentialScan);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[1024 * 1024];
        while (true)
        {
            int read = input.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                break;
            }

            output.Write(buffer, 0, read);
            hash.AppendData(buffer, 0, read);
        }

        output.Flush(flushToDisk: true);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    internal static long DirectorySize(string root)
    {
        if (!Directory.Exists(root))
        {
            return 0;
        }

        EnsureNoReparsePoints(root);
        return new DirectoryInfo(root).EnumerateFiles("*", SearchOption.AllDirectories).Sum(file => file.Length);
    }

    internal static void ExtractProduct(string packagePath, string destinationRoot)
    {
        FileInfo package = new(FullPath(packagePath));
        if (!package.Exists || !string.Equals(package.Extension, ".zip", StringComparison.OrdinalIgnoreCase))
        {
            throw new FileNotFoundException("The FoxMouse package must be a ZIP file.", package.FullName);
        }

        RequireSafeLeaf(destinationRoot, "package extraction root");
        Directory.CreateDirectory(destinationRoot);
        string destinationPrefix = FullPath(destinationRoot) + Path.DirectorySeparatorChar;
        using ZipArchive archive = ZipFile.OpenRead(package.FullName);
        if (archive.Entries.Count > MaxArchiveEntries)
        {
            throw new InvalidDataException($"FoxMouse package contains too many entries ({archive.Entries.Count}; maximum {MaxArchiveEntries}).");
        }

        List<ValidatedArchiveEntry> validatedEntries = [];
        Dictionary<string, bool> targetKinds = new(StringComparer.OrdinalIgnoreCase);
        long totalExpandedBytes = 0;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string normalizedName = entry.FullName.Replace('\\', '/');
            string[] segments = normalizedName.Split('/');
            bool isDirectory = normalizedName.EndsWith("/", StringComparison.Ordinal);
            if (normalizedName.StartsWith('/') ||
                !normalizedName.StartsWith("FoxMouse/", StringComparison.Ordinal) ||
                segments.Any(segment => segment is "." or ".." || segment.Contains(':')) ||
                segments.SkipLast(isDirectory ? 1 : 0).Any(string.IsNullOrEmpty))
            {
                throw new InvalidDataException($"Unsafe or unexpected ZIP entry: {entry.FullName}");
            }

            string relativeName = normalizedName["FoxMouse/".Length..];
            if (relativeName.Length == 0)
            {
                continue;
            }

            string target = Path.GetFullPath(Path.Combine(destinationRoot, relativeName.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(destinationPrefix, StringComparison.OrdinalIgnoreCase) || !targetKinds.TryAdd(target, isDirectory))
            {
                throw new InvalidDataException($"Unsafe or duplicate ZIP target: {entry.FullName}");
            }

            if (!isDirectory)
            {
                if (entry.Length > MaxArchiveEntryBytes)
                {
                    throw new InvalidDataException($"ZIP entry exceeds the per-file expansion limit: {entry.FullName}");
                }

                totalExpandedBytes = checked(totalExpandedBytes + entry.Length);
                if (totalExpandedBytes > MaxArchiveExpandedBytes)
                {
                    throw new InvalidDataException("FoxMouse package exceeds the cumulative expansion limit.");
                }
            }

            validatedEntries.Add(new ValidatedArchiveEntry(entry, target, isDirectory));
        }

        foreach (ValidatedArchiveEntry item in validatedEntries)
        {
            string? parent = Path.GetDirectoryName(item.TargetPath);
            while (!string.IsNullOrEmpty(parent) &&
                   parent.StartsWith(destinationPrefix, StringComparison.OrdinalIgnoreCase))
            {
                if (targetKinds.TryGetValue(parent, out bool parentIsDirectory) && !parentIsDirectory)
                {
                    throw new InvalidDataException($"ZIP file/directory target conflict: {item.Entry.FullName}");
                }

                parent = Path.GetDirectoryName(parent);
            }

            if (!item.IsDirectory && validatedEntries.Any(candidate =>
                    candidate.TargetPath.StartsWith(item.TargetPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidDataException($"ZIP file/directory target conflict: {item.Entry.FullName}");
            }
        }

        foreach (ValidatedArchiveEntry item in validatedEntries)
        {
            if (item.IsDirectory)
            {
                Directory.CreateDirectory(item.TargetPath);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(item.TargetPath)!);
            using Stream source = item.Entry.Open();
            using FileStream destination = new(item.TargetPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            byte[] buffer = new byte[1024 * 1024];
            long written = 0;
            while (true)
            {
                int read = source.Read(buffer, 0, buffer.Length);
                if (read == 0)
                {
                    break;
                }

                written = checked(written + read);
                if (written > item.Entry.Length || written > MaxArchiveEntryBytes)
                {
                    throw new InvalidDataException($"ZIP entry expanded beyond its declared or permitted size: {item.Entry.FullName}");
                }

                destination.Write(buffer, 0, read);
            }

            if (written != item.Entry.Length)
            {
                throw new InvalidDataException($"ZIP entry length changed during extraction: {item.Entry.FullName}");
            }
        }

        foreach (string required in RequiredProductFiles)
        {
            string requiredPath = Path.Combine(destinationRoot, required);
            if (!File.Exists(requiredPath))
            {
                throw new InvalidDataException($"FoxMouse package is missing required file: {required}");
            }
        }
    }

    internal static readonly string[] RequiredProductFiles =
    [
        "FoxMouse.exe",
        "FoxMouse.Guard.exe",
        Path.Combine("Settings", "FoxMouse.Settings.exe"),
        "FoxMouse.Uninstall.exe",
        "FoxMouse.Cleanup.exe",
    ];

    private sealed record ValidatedArchiveEntry(ZipArchiveEntry Entry, string TargetPath, bool IsDirectory);
}
