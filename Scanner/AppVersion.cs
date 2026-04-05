using System.Reflection;

namespace ArbiScan.Scanner;

public static class AppVersion
{
    public static string Current { get; } = ResolveCurrent();
    public static string ProductName => $"ArbiScan v{Current}";

    private static string ResolveCurrent()
    {
        var environmentVersion = Environment.GetEnvironmentVariable("ARBISCAN_VERSION");
        if (!string.IsNullOrWhiteSpace(environmentVersion))
        {
            return environmentVersion.Trim();
        }

        var assembly = Assembly.GetEntryAssembly() ?? typeof(AppVersion).Assembly;
        var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            var buildMetadataSeparator = informationalVersion.IndexOf('+');
            return buildMetadataSeparator >= 0
                ? informationalVersion[..buildMetadataSeparator]
                : informationalVersion;
        }

        return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }
}
