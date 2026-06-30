using System;
using System.IO;
using System.Text.Json;

namespace AI_desktop_tool
{
    public static class ConfigHelper
    {
        private static readonly string ConfigPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");

        public static AppConfig LoadConfig()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    string json = File.ReadAllText(ConfigPath);
                    AppConfig? config = JsonSerializer.Deserialize<AppConfig>(json);
                    if (config != null)
                    {
                        return config;
                    }
                }
            }
            catch (Exception)
            {
                // Fail silently and return defaults
            }
            
            // Green/portable default: no config.json is required for first run.
            // A config file is only written when the user explicitly saves settings.
            return new AppConfig();
        }

        public static void SaveConfig(AppConfig config)
        {
            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(config, options);
                File.WriteAllText(ConfigPath, json);
            }
            catch (Exception)
            {
                // Fail silently
            }
        }
    }
}
