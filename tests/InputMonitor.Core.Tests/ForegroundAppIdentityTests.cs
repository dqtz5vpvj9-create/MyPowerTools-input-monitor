using InputMonitor.Core;

namespace InputMonitor.Core.Tests;

/// <summary>
/// Pins the foreground app identity rules restored from the Windows branch
/// (commit 72b166b, <c>ForegroundAppIdentity</c>): what the tracker stores as bundle id/display name,
/// and when a resolved session must be left alone instead of being re-attributed.
///
/// Paths are built with <see cref="Path.Combine"/> so the test asserts the same behaviour on Windows and
/// on the Linux CI runner; the production code only ever runs on Windows, where the module is loaded.
/// </summary>
public sealed class ForegroundAppIdentityTests
{
    [Fact]
    public void Image_path_wins_and_the_file_description_is_the_display_name()
    {
        var image = Path.Combine("Apps", "Chrome.EXE");

        var (bundleId, appName) = ForegroundAppIdentity.Resolve(image, "Google Chrome", "chrome", "Some title");

        Assert.Equal("chrome.exe", bundleId);
        Assert.Equal("Google Chrome", appName);
    }

    [Fact]
    public void Bundle_id_is_lowercased_and_the_name_falls_back_to_the_file_name()
    {
        var (bundleId, appName) = ForegroundAppIdentity.Resolve(
            Path.Combine("Apps", "Notepad.EXE"),
            fileDescription: null,
            processName: "ignored",
            windowTitle: "ignored");

        Assert.Equal("notepad.exe", bundleId);
        Assert.Equal("Notepad", appName);
    }

    [Fact]
    public void Process_name_is_used_when_no_image_path_is_reported()
    {
        var (bundleId, appName) = ForegroundAppIdentity.Resolve(null, null, "Notepad", "Untitled - Notepad");

        Assert.Equal("notepad", bundleId);
        Assert.Equal("Notepad", appName);
    }

    [Fact]
    public void Window_title_is_the_last_resort_before_unknown()
    {
        var (bundleId, appName) = ForegroundAppIdentity.Resolve(null, null, null, "  Untitled - Notepad  ");

        Assert.Equal("unknown", bundleId);
        Assert.Equal("Untitled - Notepad", appName);
    }

    [Fact]
    public void Nothing_usable_resolves_to_unknown()
    {
        var (bundleId, appName) = ForegroundAppIdentity.Resolve("   ", "  ", null, null);

        Assert.Equal("unknown", bundleId);
        Assert.Equal("Unknown", appName);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("unknown", true)]
    [InlineData("UNKNOWN", true)]
    [InlineData("chrome.exe", false)]
    public void Unresolved_ids_are_recognised(string? bundleId, bool expected) =>
        Assert.Equal(expected, ForegroundAppIdentity.IsUnresolved(bundleId));

    [Fact]
    public void A_failed_probe_leaves_the_resolved_session_alone()
    {
        // Probe failures and protected processes report "unknown"; heartbeating the previous app would
        // mis-attribute the usage, so the resolved session must be left instead of continued.
        Assert.True(ForegroundAppIdentity.MustLeaveResolvedSession("chrome.exe", "unknown"));
        Assert.True(ForegroundAppIdentity.MustLeaveResolvedSession("chrome.exe", null));
        Assert.False(ForegroundAppIdentity.MustLeaveResolvedSession("chrome.exe", "notepad.exe"));
        Assert.False(ForegroundAppIdentity.MustLeaveResolvedSession("unknown", "unknown"));
        Assert.False(ForegroundAppIdentity.MustLeaveResolvedSession(null, null));
    }
}
