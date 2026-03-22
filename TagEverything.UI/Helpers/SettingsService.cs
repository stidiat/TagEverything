using System;
using System.IO;
using Newtonsoft.Json;

namespace TagEverything.UI.Helpers
{
    public class AppSettings
    {
        public string LibraryPath { get; set; } = string.Empty;
    }

    public class SettingsService
    {
        private readonly string _settingsFile;
        public AppSettings Current { get; private set; }

        public SettingsService()
        {
            _settingsFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");
            Load();
        }

        public void Load()
        {
            if (File.Exists(_settingsFile))
            {
                var json = File.ReadAllText(_settingsFile);
                Current = JsonConvert.DeserializeObject<AppSettings>(json) ?? new AppSettings();
            }
            else
            {
                Current = new AppSettings();
            }
        }

        public void Save()
        {
            var json = JsonConvert.SerializeObject(Current, Formatting.Indented);
            File.WriteAllText(_settingsFile, json);
        }
    }
}
