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
            
            // If file does not exist or fails to load, create it with default values
            AppConfig defaultConfig = new AppConfig();
            SaveConfig(defaultConfig);
            return defaultConfig;
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
