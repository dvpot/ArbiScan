using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Nodes;
using ArbiScan.Infrastructure.Setup;

namespace ArbiScan.Scanner;

public sealed class AppSettingsCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _currentSettingsPath;
    private readonly string _catalogPath;

    public AppSettingsCatalog(AppStoragePaths storagePaths)
    {
        _currentSettingsPath = Path.Combine(storagePaths.ConfigPath, "appsettings.json");
        _catalogPath = Path.Combine(storagePaths.ConfigPath, "appsettings");
        Directory.CreateDirectory(_catalogPath);
    }

    public string CatalogPath => _catalogPath;

    public async Task<string> GetCurrentSettingsAsync(CancellationToken cancellationToken)
    {
        EnsureCurrentSettingsExists();
        return await File.ReadAllTextAsync(_currentSettingsPath, cancellationToken);
    }

    public async Task<string?> GetMatchingPresetNameAsync(CancellationToken cancellationToken)
    {
        var currentNormalized = NormalizeJson(await GetCurrentSettingsAsync(cancellationToken));

        foreach (var presetPath in GetPresetPaths())
        {
            var presetNormalized = NormalizeJson(await File.ReadAllTextAsync(presetPath, cancellationToken));
            if (string.Equals(currentNormalized, presetNormalized, StringComparison.Ordinal))
            {
                return Path.GetFileNameWithoutExtension(presetPath);
            }
        }

        return null;
    }

    public IReadOnlyList<string> ListPresetNames() =>
        GetPresetPaths()
            .Select(Path.GetFileNameWithoutExtension)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Cast<string>()
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public async Task<string> UpsertPresetAsync(string presetName, string json, CancellationToken cancellationToken)
    {
        var normalizedName = NormalizePresetName(presetName);
        var normalizedJson = NormalizeAndValidateAppSettingsJson(json);
        var presetPath = GetPresetPath(normalizedName);
        await File.WriteAllTextAsync(presetPath, normalizedJson, cancellationToken);
        return normalizedName;
    }

    public async Task<string> SaveCurrentAsPresetAsync(string presetName, CancellationToken cancellationToken)
    {
        var normalizedName = NormalizePresetName(presetName);
        var currentJson = await GetCurrentSettingsAsync(cancellationToken);
        var normalizedJson = NormalizeAndValidateAppSettingsJson(currentJson);
        await File.WriteAllTextAsync(GetPresetPath(normalizedName), normalizedJson, cancellationToken);
        return normalizedName;
    }

    public async Task<string> ActivatePresetAsync(string presetName, CancellationToken cancellationToken)
    {
        var normalizedName = NormalizePresetName(presetName);
        var presetPath = GetPresetPath(normalizedName);
        if (!File.Exists(presetPath))
        {
            throw new FileNotFoundException($"Preset '{normalizedName}' не найден.", presetPath);
        }

        var normalizedJson = NormalizeAndValidateAppSettingsJson(await File.ReadAllTextAsync(presetPath, cancellationToken));
        await File.WriteAllTextAsync(_currentSettingsPath, normalizedJson, cancellationToken);
        return normalizedName;
    }

    public async Task PatchCurrentSettingsAsync(string path, string jsonValue, CancellationToken cancellationToken)
    {
        EnsureCurrentSettingsExists();
        var currentJson = await File.ReadAllTextAsync(_currentSettingsPath, cancellationToken);
        var rootNode = JsonNode.Parse(currentJson)?.AsObject()
            ?? throw new ValidationException("Текущий appsettings.json не удалось разобрать.");

        var segments = path
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        if (segments.Count == 0)
        {
            throw new ValidationException("Нужно указать путь к настройке, например Symbol или Storage.RootPath.");
        }

        if (!string.Equals(segments[0], "ArbiScan", StringComparison.OrdinalIgnoreCase))
        {
            segments.Insert(0, "ArbiScan");
        }

        var current = rootNode;
        for (var index = 0; index < segments.Count - 1; index++)
        {
            var segment = segments[index];
            if (current[segment] is not JsonObject child)
            {
                child = new JsonObject();
                current[segment] = child;
            }

            current = child;
        }

        current[segments[^1]] = ParseJsonValue(jsonValue);

        var normalizedJson = NormalizeAndValidateAppSettingsJson(rootNode.ToJsonString(JsonOptions));
        await File.WriteAllTextAsync(_currentSettingsPath, normalizedJson, cancellationToken);
    }

    private IEnumerable<string> GetPresetPaths() =>
        Directory.Exists(_catalogPath)
            ? Directory.EnumerateFiles(_catalogPath, "*.json", SearchOption.TopDirectoryOnly)
            : Array.Empty<string>();

    private string GetPresetPath(string presetName) =>
        Path.Combine(_catalogPath, $"{presetName}.json");

    private static string NormalizePresetName(string presetName)
    {
        var trimmed = presetName.Trim();
        if (trimmed.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^5];
        }

        if (string.IsNullOrWhiteSpace(trimmed))
        {
            throw new ValidationException("Имя preset не должно быть пустым.");
        }

        if (trimmed.Any(ch => !char.IsLetterOrDigit(ch) && ch is not '-' and not '_' and not '.'))
        {
            throw new ValidationException("Имя preset может содержать только буквы, цифры, '.', '-' и '_'.");
        }

        return trimmed;
    }

    private static JsonNode? ParseJsonValue(string rawValue)
    {
        try
        {
            return JsonNode.Parse(rawValue);
        }
        catch (JsonException)
        {
            return JsonValue.Create(rawValue);
        }
    }

    private static string NormalizeAndValidateAppSettingsJson(string json)
    {
        SettingsValidator.ParseAndValidateAppSettingsJson(json);
        return NormalizeJson(json);
    }

    private static string NormalizeJson(string json)
    {
        var node = JsonNode.Parse(json)
            ?? throw new ValidationException("JSON не удалось разобрать.");
        return node.ToJsonString(JsonOptions);
    }

    private void EnsureCurrentSettingsExists()
    {
        if (!File.Exists(_currentSettingsPath))
        {
            throw new FileNotFoundException("Текущий appsettings.json не найден.", _currentSettingsPath);
        }
    }
}
