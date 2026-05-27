using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using TeamsChannelScraper.WPF.Models;

namespace TeamsChannelScraper.WPF.Utilities;

public sealed class ConfigManager
{
    private readonly string _configPath;
    private readonly string _emailPath;

    public ConfigManager()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Constants.AppDataFolder);
        Directory.CreateDirectory(dir);
        _configPath = Path.Combine(dir, "config.json");
        _emailPath  = Path.Combine(dir, "email.dat");
    }

    public void SaveConfig(ScrapingConfig config)
    {
        // Save non-sensitive fields as JSON
        var dto = new ConfigDto
        {
            TeamName          = config.TeamName,
            ChannelName       = config.ChannelName,
            MaxMessages       = config.MaxMessages,
            IncludeReplies    = config.IncludeReplies,
            IncludeAttachments = config.IncludeAttachments,
            UseHeadlessBrowser = config.UseHeadlessBrowser
        };
        File.WriteAllText(_configPath, JsonConvert.SerializeObject(dto, Formatting.Indented));

        // Save email separately via DPAPI
        if (!string.IsNullOrEmpty(config.UserEmail))
            SaveEmail(config.UserEmail);
    }

    public ScrapingConfig LoadConfig()
    {
        if (!File.Exists(_configPath))
            return new ScrapingConfig { MaxMessages = Constants.DefaultMaxMessages };

        var dto = JsonConvert.DeserializeObject<ConfigDto>(File.ReadAllText(_configPath))
                  ?? new ConfigDto();

        return new ScrapingConfig
        {
            TeamName           = dto.TeamName,
            ChannelName        = dto.ChannelName,
            UserEmail          = LoadEmail() ?? string.Empty,
            MaxMessages        = dto.MaxMessages,
            IncludeReplies     = dto.IncludeReplies,
            IncludeAttachments = dto.IncludeAttachments,
            UseHeadlessBrowser = dto.UseHeadlessBrowser
        };
    }

    private void SaveEmail(string email)
    {
        var encrypted = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(email),
            null,
            DataProtectionScope.CurrentUser);
        File.WriteAllBytes(_emailPath, encrypted);
    }

    private string? LoadEmail()
    {
        if (!File.Exists(_emailPath)) return null;
        try
        {
            var decrypted = ProtectedData.Unprotect(
                File.ReadAllBytes(_emailPath),
                null,
                DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(decrypted);
        }
        catch { return null; }
    }

    private sealed class ConfigDto
    {
        public string TeamName           { get; set; } = string.Empty;
        public string ChannelName        { get; set; } = string.Empty;
        public int    MaxMessages        { get; set; } = Constants.DefaultMaxMessages;
        public bool   IncludeReplies     { get; set; } = true;
        public bool   IncludeAttachments { get; set; } = false;
        public bool   UseHeadlessBrowser { get; set; } = false;
    }
}
