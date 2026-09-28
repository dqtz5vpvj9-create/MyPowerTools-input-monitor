namespace InputMonitor.Core;

/// <summary>
/// Turns a foreground process image into the bundle id and display name stored for app usage.
/// </summary>
public static class ForegroundAppIdentity
{
    public static bool IsUnresolved(string? bundleId) =>
        string.IsNullOrWhiteSpace(bundleId) ||
        string.Equals(bundleId, "unknown", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Probe failures and protected processes report as unresolved. Continuing to heartbeat the
    /// previous resolved session would mis-attribute usage; leave it instead.
    /// </summary>
    public static bool MustLeaveResolvedSession(string? currentBundleId, string? probeBundleId) =>
        !IsUnresolved(currentBundleId) && IsUnresolved(probeBundleId);

    public static (string BundleId, string AppName) Resolve(
        string? imagePath,
        string? fileDescription,
        string? processName,
        string? windowTitle)
    {
        if (!string.IsNullOrWhiteSpace(imagePath))
        {
            var fileName = Path.GetFileName(imagePath.Trim());
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                return (fileName.ToLowerInvariant(), First(fileDescription, Path.GetFileNameWithoutExtension(fileName), processName, windowTitle));
            }
        }

        if (!string.IsNullOrWhiteSpace(processName))
        {
            var name = processName.Trim();
            return (name.ToLowerInvariant(), name);
        }

        if (!string.IsNullOrWhiteSpace(windowTitle))
        {
            return ("unknown", windowTitle.Trim());
        }

        return ("unknown", "Unknown");
    }

    private static string First(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return "Unknown";
    }
}
