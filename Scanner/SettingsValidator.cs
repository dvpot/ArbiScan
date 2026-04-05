using System.ComponentModel.DataAnnotations;
using System.Text;
using ArbiScan.Core.Configuration;
using Microsoft.Extensions.Configuration;

namespace ArbiScan.Scanner;

public static class SettingsValidator
{
    public static void ValidateAppSettings(AppSettings settings)
    {
        var validationResults = new List<ValidationResult>();
        var validationContext = new ValidationContext(settings);
        if (!Validator.TryValidateObject(settings, validationContext, validationResults, true))
        {
            throw new ValidationException(string.Join(Environment.NewLine, validationResults.Select(x => x.ErrorMessage)));
        }

        if (settings.TestNotionalsUsd.Any(x => x <= 0m))
        {
            throw new ValidationException("Все test notionals должны быть положительными.");
        }
    }

    public static void ValidateTelegramSettings(TelegramSettings settings)
    {
        if (!settings.Enabled)
        {
            return;
        }

        var validationResults = new List<ValidationResult>();
        var validationContext = new ValidationContext(settings);
        if (!Validator.TryValidateObject(settings, validationContext, validationResults, true))
        {
            throw new ValidationException(string.Join(Environment.NewLine, validationResults.Select(x => x.ErrorMessage)));
        }

        if (settings.AllowedUserId == 0)
        {
            throw new ValidationException("TelegramBot:AllowedUserId должен быть задан, когда Telegram включён.");
        }
    }

    public static AppSettings ParseAndValidateAppSettingsJson(string json)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var configuration = new ConfigurationBuilder()
            .AddJsonStream(stream)
            .Build();

        var section = configuration.GetSection("ArbiScan");
        if (!section.Exists())
        {
            throw new ValidationException("JSON должен содержать корневую секцию ArbiScan.");
        }

        var settings = new AppSettings();
        section.Bind(settings);
        ValidateAppSettings(settings);
        return settings;
    }
}
