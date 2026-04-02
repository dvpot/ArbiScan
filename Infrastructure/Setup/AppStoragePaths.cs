namespace ArbiScan.Infrastructure.Setup;

public sealed record AppStoragePaths(
    string RootPath,
    string ConfigPath,
    string LogsPath,
    string DataPath,
    string ReportsPath,
    string DatabasePath);
