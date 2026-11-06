using System;
using System.IO;
using Newtonsoft.Json;

namespace TagEverything.UI.Helpers
{
    public class AppSettings
    {
        public string LibraryPath { get; set; } = string.Empty;
        public int SimilarityThreshold { get; set; } = 10;
    }

    public class SettingsService
    {
        private readonly string _settingsFile;
        private readonly string _legacySettingsFile;
        public AppSettings Current { get; private set; } = new();

        public SettingsService(string? settingsDirectory = null)
        {
            var useLegacy = settingsDirectory == null;
            settingsDirectory ??= Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TagEverything");

            _settingsFile = Path.Combine(settingsDirectory, "settings.json");
            _legacySettingsFile = Path.Combine(useLegacy ? AppDomain.CurrentDomain.BaseDirectory : settingsDirectory, "legacy.settings.json");
            Load();
        }

        public void Load()
        {
            Current = TryLoad(_settingsFile)
                ?? TryLoad(_legacySettingsFile)
                ?? new AppSettings();

            // Migrate a valid legacy configuration out of the installation folder.
            if (!File.Exists(_settingsFile) && !string.IsNullOrWhiteSpace(Current.LibraryPath))
            {
                Save();
            }
        }

        public void Save()
        {
            var directory = Path.GetDirectoryName(_settingsFile)!;
            Directory.CreateDirectory(directory);

            var json = JsonConvert.SerializeObject(Current, Formatting.Indented);
            var temporaryFile = _settingsFile + ".tmp";
            File.WriteAllText(temporaryFile, json);
            File.Move(temporaryFile, _settingsFile, true);
        }

        private static AppSettings? TryLoad(string path)
        {
            if (!File.Exists(path)) return null;

            try
            {
                var json = File.ReadAllText(path);
                return JsonConvert.DeserializeObject<AppSettings>(json);
            }
            catch (JsonException)
            {
                return null;
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }
    }
}
