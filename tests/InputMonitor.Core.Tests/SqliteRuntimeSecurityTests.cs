using Xunit.Abstractions;

namespace InputMonitor.Core.Tests;

public sealed class SqliteRuntimeSecurityTests(ITestOutputHelper output)
{
    [Fact]
    public void Loaded_sqlite_runtime_contains_the_CVE_2025_6965_fix()
    {
        using var database = new MonitorDatabase(":memory:");
        var actual = database.Run(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT sqlite_version();";
            return (string)command.ExecuteScalar()!;
        });

        output.WriteLine($"Loaded SQLite native runtime: {actual}");
        // GHSA-2m69-gcr7-jv3q requires SQLite >= 3.50.2. Check the native
        // engine actually loaded by Microsoft.Data.Sqlite, not a managed version.
        Assert.True(Version.TryParse(actual, out var version) && version >= new Version(3, 50, 2),
            $"Loaded SQLite {actual}; SQLite >= 3.50.2 is required for CVE-2025-6965.");
    }

    [Theory]
    [InlineData("win-x64")]
    [InlineData("win-arm64")]
    [InlineData("win-x86")]
    public void Windows_native_assets_preserve_the_module_packaging_paths(string runtimeIdentifier)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "runtimes", runtimeIdentifier, "native", "e_sqlite3.dll");
        // InputMonitor.MyPowerTools.csproj stages these exact paths into the module.
        Assert.True(File.Exists(path), $"Missing SQLite packaging asset: {path}");
        Assert.True(new FileInfo(path).Length > 0, $"Empty SQLite packaging asset: {path}");
    }
}
