using ArbiScan.Core.Configuration;

namespace ArbiScan.Infrastructure.Setup;

public static class StoragePathBuilder
{
    public static AppStoragePaths Build(StorageSettings settings)
    {
        var rootPath = Path.GetFullPath(settings.RootPath);
        var configPath = Path.Combine(rootPath, settings.ConfigDirectoryName);
        var logsPath = Path.Combine(rootPath, settings.LogsDirectoryName);
        var dataPath = Path.Combine(rootPath, settings.DataDirectoryName);
        var reportsPath = Path.Combine(rootPath, settings.ReportsDirectoryName);
        var databasePath = Path.Combine(dataPath, settings.DatabaseFileName);

        Directory.CreateDirectory(rootPath);
        Directory.CreateDirectory(configPath);
        Directory.CreateDirectory(logsPath);
        Directory.CreateDirectory(dataPath);
        Directory.CreateDirectory(reportsPath);

        return new AppStoragePaths(rootPath, configPath, logsPath, dataPath, reportsPath, databasePath);
    }
}
