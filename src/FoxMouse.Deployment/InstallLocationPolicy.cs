namespace FoxMouse.Deployment;

/// <summary>
/// Normalizes and validates user-selectable per-user installation locations.
/// The user selects a parent directory; FoxMouse always owns exactly one
/// child directory named <c>FoxMouse</c> below that parent.
/// </summary>
public static class InstallLocationPolicy
{
    public const string ProductDirectoryName = "FoxMouse";

    public static string ResolveInstallRoot(string selectedParent, DeploymentPaths basePaths)
    {
        ArgumentNullException.ThrowIfNull(basePaths);
        DeploymentPaths normalizedPaths = basePaths.NormalizeAndValidate();
        string parent = NormalizeLocalAbsolutePath(selectedParent, "installation parent");
        string root = string.Equals(Path.GetFileName(parent), ProductDirectoryName, StringComparison.OrdinalIgnoreCase)
            ? parent
            : DeploymentFileSystem.FullPath(Path.Combine(parent, ProductDirectoryName));

        ValidateRootBoundary(root, normalizedPaths);
        ValidateNewTarget(root);
        EnsureWritableParent(root);
        return root;
    }

    public static DeploymentPaths ResolvePathsForParent(string selectedParent, DeploymentPaths basePaths) =>
        basePaths.WithInstallRoot(ResolveInstallRoot(selectedParent, basePaths));

    /// <summary>
    /// Validates a root immediately before a deployment transaction. Existing
    /// contents are accepted only when the caller has already established that
    /// they belong to FoxMouse.
    /// </summary>
    internal static void ValidateOperationRoot(
        DeploymentPaths paths,
        bool allowExistingProductContents)
    {
        DeploymentPaths normalized = paths.NormalizeAndValidate();
        string root = NormalizeLocalAbsolutePath(normalized.InstallRoot, "install root");
        ValidateRootBoundary(root, normalized);
        EnsureNoReparsePointInPath(root);

        if (File.Exists(root))
        {
            throw new InvalidOperationException($"The FoxMouse install target is a file: {root}");
        }

        if (Directory.Exists(root))
        {
            DeploymentFileSystem.EnsureNoReparsePoints(root);
            bool hasContents;
            try
            {
                hasContents = Directory.EnumerateFileSystemEntries(root).Any();
            }
            catch (UnauthorizedAccessException exception)
            {
                throw new InvalidOperationException($"The FoxMouse install target cannot be inspected: {root}", exception);
            }

            if (hasContents && !allowExistingProductContents)
            {
                throw new InvalidOperationException(
                    $"The FoxMouse install target is not empty and is not a recognized FoxMouse installation: {root}");
            }
        }

        EnsureWritableParent(root);
    }

    internal static bool IsPathWithin(string path, string root)
    {
        string candidate = DeploymentFileSystem.FullPath(path);
        string boundary = DeploymentFileSystem.FullPath(root);
        return string.Equals(candidate, boundary, StringComparison.OrdinalIgnoreCase) ||
            candidate.StartsWith(boundary + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static void ValidateNewTarget(string root)
    {
        EnsureNoReparsePointInPath(root);
        if (File.Exists(root))
        {
            throw new InvalidOperationException($"The FoxMouse install target is a file: {root}");
        }

        if (!Directory.Exists(root))
        {
            return;
        }

        DeploymentFileSystem.EnsureNoReparsePoints(root);
        try
        {
            if (Directory.EnumerateFileSystemEntries(root).Any())
            {
                throw new InvalidOperationException(
                    $"The selected FoxMouse folder is not empty. Choose another parent directory: {root}");
            }
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new InvalidOperationException($"The selected FoxMouse folder cannot be inspected: {root}", exception);
        }
    }

    private static string NormalizeLocalAbsolutePath(string path, string label)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException($"The {label} must be an absolute local path.", nameof(path));
        }

        if (path.StartsWith(@"\\", StringComparison.Ordinal) ||
            path.StartsWith("//", StringComparison.Ordinal) ||
            path.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            path.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            throw new ArgumentException($"The {label} must not use a network or device path.", nameof(path));
        }

        string normalized = DeploymentFileSystem.FullPath(path);
        string? pathRoot = Path.GetPathRoot(normalized);
        if (string.IsNullOrWhiteSpace(pathRoot) || pathRoot.StartsWith(@"\\", StringComparison.Ordinal))
        {
            throw new ArgumentException($"The {label} must be on a local drive.", nameof(path));
        }

        try
        {
            DriveInfo drive = new(pathRoot);
            if (drive.DriveType is DriveType.Network or DriveType.CDRom or DriveType.NoRootDirectory)
            {
                throw new InvalidOperationException($"The {label} must be on a writable local drive.");
            }
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentException($"The {label} does not identify a valid local drive.", nameof(path), exception);
        }

        return normalized;
    }

    private static void ValidateRootBoundary(string root, DeploymentPaths paths)
    {
        if (!string.Equals(Path.GetFileName(root), ProductDirectoryName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"The FoxMouse install root must end with exactly one '{ProductDirectoryName}' directory.");
        }

        foreach (string protectedRoot in GetProtectedRoots())
        {
            if (IsPathWithin(root, protectedRoot))
            {
                throw new InvalidOperationException(
                    $"FoxMouse cannot be installed inside the protected directory: {protectedRoot}");
            }
        }

        string shortcutDirectory = Path.GetDirectoryName(paths.StartMenuShortcut)!;
        foreach ((string managedPath, string label) in new[]
        {
            (paths.SettingsRoot, "settings"),
            (paths.InstallerCacheRoot, "installer cache"),
            (shortcutDirectory, "Start menu"),
        })
        {
            if (IsPathWithin(root, managedPath) || IsPathWithin(managedPath, root))
            {
                throw new InvalidOperationException(
                    $"The FoxMouse install root must not overlap the {label} location.");
            }
        }

        if (paths.IsIsolated && !IsPathWithin(root, paths.IsolationRoot!))
        {
            throw new InvalidOperationException("The selected install location escapes the isolated test root.");
        }
    }

    private static IEnumerable<string> GetProtectedRoots()
    {
        HashSet<string> values = new(StringComparer.OrdinalIgnoreCase);
        foreach (string candidate in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.SystemDirectory,
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        })
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                values.Add(DeploymentFileSystem.FullPath(candidate));
            }
        }

        return values;
    }

    private static void EnsureNoReparsePointInPath(string root)
    {
        string? current = root;
        while (!string.IsNullOrWhiteSpace(current))
        {
            if (File.Exists(current))
            {
                throw new InvalidOperationException($"A file blocks the selected installation path: {current}");
            }

            if (Directory.Exists(current))
            {
                FileAttributes attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidOperationException($"Reparse-point installation paths are not allowed: {current}");
                }
            }

            string? parent = Path.GetDirectoryName(current);
            if (string.IsNullOrWhiteSpace(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            current = parent;
        }
    }

    private static void EnsureWritableParent(string installRoot)
    {
        string? candidate = Path.GetDirectoryName(installRoot);
        while (!string.IsNullOrWhiteSpace(candidate) && !Directory.Exists(candidate))
        {
            if (File.Exists(candidate))
            {
                throw new InvalidOperationException($"A file blocks the selected installation path: {candidate}");
            }

            candidate = Path.GetDirectoryName(candidate);
        }

        if (string.IsNullOrWhiteSpace(candidate))
        {
            throw new InvalidOperationException("No writable parent directory exists for the selected installation path.");
        }

        string probe = Path.Combine(candidate, $".FoxMouse.write-test.{Guid.NewGuid():N}");
        string movedProbe = probe + ".moved";
        try
        {
            Directory.CreateDirectory(probe);
            Directory.Move(probe, movedProbe);
            Directory.Delete(movedProbe, recursive: false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"The selected installation parent is not writable: {candidate}",
                exception);
        }
        finally
        {
            try
            {
                if (Directory.Exists(probe))
                {
                    Directory.Delete(probe, recursive: false);
                }

                if (Directory.Exists(movedProbe))
                {
                    Directory.Delete(movedProbe, recursive: false);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
