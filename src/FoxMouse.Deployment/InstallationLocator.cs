namespace FoxMouse.Deployment;

/// <summary>
/// Resolves the one authoritative FoxMouse install root for every entry point.
/// A validated running root uninstaller is authoritative, followed by HKCU
/// Installed Apps state and then the historical per-user default directory.
/// </summary>
public static class InstallationLocator
{
    public static DeploymentPaths ResolveForSetup(
        DeploymentPaths defaultPaths,
        string? selectedParent = null,
        string? currentExecutable = null)
    {
        DeploymentPaths defaults = defaultPaths.NormalizeAndValidate();
        DeploymentPaths? existing = TryResolveSelf(defaults, currentExecutable) ??
            TryResolveRegistered(defaults) ??
            TryResolveDefault(defaults);

        if (existing is not null)
        {
            if (!string.IsNullOrWhiteSpace(selectedParent))
            {
                throw new InvalidOperationException(
                    "FoxMouse is already installed. Uninstall it before choosing a different installation location.");
            }

            return existing;
        }

        if (!string.IsNullOrWhiteSpace(selectedParent))
        {
            return InstallLocationPolicy.ResolvePathsForParent(selectedParent, defaults);
        }

        InstallLocationPolicy.ValidateOperationRoot(defaults, allowExistingProductContents: false);
        return defaults;
    }

    public static DeploymentPaths ResolveForUninstall(
        DeploymentPaths defaultPaths,
        string? explicitInstallRoot = null,
        string? currentExecutable = null)
    {
        DeploymentPaths defaults = defaultPaths.NormalizeAndValidate();
        DeploymentPaths? self = TryResolveSelf(defaults, currentExecutable);
        if (self is not null)
        {
            if (!string.IsNullOrWhiteSpace(explicitInstallRoot) &&
                !PathsEqual(self.InstallRoot, explicitInstallRoot))
            {
                throw new InvalidDataException(
                    "The supplied FoxMouse install root does not match the running root uninstaller.");
            }

            return self;
        }

        if (!string.IsNullOrWhiteSpace(explicitInstallRoot))
        {
            return ResolveExplicit(defaults, explicitInstallRoot);
        }

        return TryResolveRegistered(defaults) ??
            TryResolveDefault(defaults) ??
            defaults;
    }

    private static DeploymentPaths? TryResolveSelf(DeploymentPaths defaults, string? currentExecutable)
    {
        if (string.IsNullOrWhiteSpace(currentExecutable))
        {
            return null;
        }

        string executable;
        try
        {
            executable = DeploymentFileSystem.FullPath(currentExecutable);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        if (!string.Equals(Path.GetFileName(executable), "FoxMouse.Uninstall.exe", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string? directory = Path.GetDirectoryName(executable);
        if (string.IsNullOrWhiteSpace(directory) ||
            !string.Equals(Path.GetFileName(directory), InstallLocationPolicy.ProductDirectoryName, StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(Path.Combine(directory, "FoxMouse.exe")) ||
            !File.Exists(Path.Combine(directory, "FoxMouse.Cleanup.exe")))
        {
            return null;
        }

        DeploymentPaths candidate = defaults.WithInstallRoot(directory);
        ValidateDiscoveredRoot(candidate, allowLegacy: true, evidence: "running root uninstaller");
        return candidate;
    }

    private static DeploymentPaths? TryResolveRegistered(DeploymentPaths defaults)
    {
        IReadOnlyDictionary<string, object?> values = ArpRegistration.ReadValues(defaults);
        if (values.Count == 0)
        {
            return null;
        }

        if (values.TryGetValue("DisplayName", out object? displayName) &&
            displayName is string name &&
            !string.Equals(name, "FoxMouse", StringComparison.Ordinal))
        {
            throw new InvalidDataException("The FoxMouse Installed Apps registration has an invalid product identity.");
        }

        if (!values.TryGetValue("InstallLocation", out object? locationValue) ||
            locationValue is not string location ||
            string.IsNullOrWhiteSpace(location))
        {
            // Older default-location builds may not have persisted this value.
            // Only the known default root is eligible for that migration path.
            return null;
        }

        DeploymentPaths candidate;
        try
        {
            candidate = defaults.WithInstallRoot(location);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidDataException("The registered FoxMouse install location is invalid.", exception);
        }

        ValidateDiscoveredRoot(candidate, allowLegacy: HasLegacyArpEvidence(values, candidate), evidence: "Installed Apps registration");
        return candidate;
    }

    private static DeploymentPaths? TryResolveDefault(DeploymentPaths defaults)
    {
        if (!Directory.Exists(defaults.InstallRoot) && !File.Exists(defaults.InstallRoot))
        {
            return null;
        }

        InstallationStatus status = new DeploymentEngine().GetStatus(defaults);
        if (status.Kind == InstallationKind.Absent)
        {
            InstallLocationPolicy.ValidateOperationRoot(defaults, allowExistingProductContents: false);
            return null;
        }

        ValidateDiscoveredRoot(defaults, allowLegacy: true, evidence: "default installation location");
        return defaults;
    }

    private static DeploymentPaths ResolveExplicit(DeploymentPaths defaults, string explicitInstallRoot)
    {
        DeploymentPaths candidate;
        try
        {
            candidate = defaults.WithInstallRoot(explicitInstallRoot);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidDataException("The supplied FoxMouse install root is invalid.", exception);
        }

        bool isDefault = PathsEqual(candidate.InstallRoot, defaults.InstallRoot);
        bool matchesRegistration = false;
        IReadOnlyDictionary<string, object?> values = ArpRegistration.ReadValues(defaults);
        if (values.TryGetValue("InstallLocation", out object? registered) && registered is string registeredPath)
        {
            try
            {
                matchesRegistration = PathsEqual(candidate.InstallRoot, registeredPath);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                throw new InvalidDataException("The registered FoxMouse install location is invalid.", exception);
            }
        }

        ValidateDiscoveredRoot(
            candidate,
            allowLegacy: isDefault || (matchesRegistration && HasLegacyArpEvidence(values, candidate)),
            evidence: "detached maintenance handoff");
        return candidate;
    }

    private static void ValidateDiscoveredRoot(
        DeploymentPaths candidate,
        bool allowLegacy,
        string evidence)
    {
        InstallationStatus status = new DeploymentEngine().GetStatus(candidate);
        bool recognized = (status.Kind == InstallationKind.Managed && status.StateSchemaVersion >= 2) ||
            (status.Kind == InstallationKind.Managed && status.StateSchemaVersion < 2 && allowLegacy) ||
            (status.Kind == InstallationKind.Legacy && allowLegacy);
        try
        {
            InstallLocationPolicy.ValidateOperationRoot(candidate, recognized);
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidDataException($"The {evidence} is not a safe FoxMouse installation root.", exception);
        }

        if (!recognized)
        {
            string reason = status.Kind switch
            {
                InstallationKind.Absent => "does not contain an installed FoxMouse application",
                InstallationKind.Invalid => "contains an invalid or location-mismatched installation state",
                InstallationKind.Legacy => "does not have sufficient legacy product identity",
                _ => "is not a recognized FoxMouse installation",
            };
            throw new InvalidDataException($"The {evidence} {reason}.");
        }
    }

    private static bool HasLegacyArpEvidence(
        IReadOnlyDictionary<string, object?> values,
        DeploymentPaths paths)
    {
        if (!values.TryGetValue("DisplayName", out object? displayName) ||
            !string.Equals(displayName as string, "FoxMouse", StringComparison.Ordinal) ||
            !File.Exists(Path.Combine(paths.InstallRoot, "FoxMouse.Uninstall.exe")))
        {
            return false;
        }

        if (!values.TryGetValue("UninstallString", out object? uninstallValue) || uninstallValue is not string uninstall)
        {
            return false;
        }

        string expected = $"\"{Path.Combine(paths.InstallRoot, "FoxMouse.Uninstall.exe")}\"";
        return string.Equals(uninstall.Trim(), expected, StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            DeploymentFileSystem.FullPath(left),
            DeploymentFileSystem.FullPath(right),
            StringComparison.OrdinalIgnoreCase);
}
