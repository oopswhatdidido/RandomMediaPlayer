using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace RandomMediaPlayer
{
    /// <summary>
    /// Persisted user settings. Serialized as JSON to
    /// %APPDATA%\RandomMediaPlayer\settings.json on app close, loaded on startup.
    /// All properties have sensible defaults so a missing or partial file is fine.
    /// </summary>
    public class AppSettings
    {
        // Mode + filter
        public string Mode { get; set; } = "Photo";
        public string Orientation { get; set; } = "All";
        public bool IncludeAnimated { get; set; }

        // Timing + size filters
        public double DelaySeconds { get; set; } = 3;
        public int MinWidth { get; set; } = 500;
        public int MinHeight { get; set; } = 500;

        // Display + playback options
        public bool AlwaysOnTop { get; set; }
        public bool ScaleToFill { get; set; }
        public bool Mute { get; set; } = true;
        // Empty string = "None / preview only".
        public string MonitorDeviceName { get; set; } = "";

        // Panic hotkey (global)
        public string PanicKey { get; set; } = "F11";
        public bool PanicCtrl { get; set; }
        public bool PanicAlt { get; set; }
        public string PanicAction { get; set; } = "Stop";

        // Hold hotkey (global)
        public string HoldKey { get; set; } = "F9";
        public bool HoldCtrl { get; set; }
        public bool HoldAlt { get; set; }

        // When true, hotkeys are observed via low-level keyboard hook so
        // multiple app instances can all respond. False = exclusive
        // RegisterHotKey claim (single-instance, but key isn't passed
        // through to other apps).
        public bool MultiInstanceHotkeys { get; set; }

        // Folder persistence
        public bool PersistFolders { get; set; }
        public List<string> Folders { get; set; } = new();

        private static string SettingsDir =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "RandomMediaPlayer");

        private static string SettingsPath =>
            Path.Combine(SettingsDir, "settings.json");

        public static AppSettings Load()
        {
            try
            {
                if (!File.Exists(SettingsPath)) return new AppSettings();
                var json = File.ReadAllText(SettingsPath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json);
                return loaded ?? new AppSettings();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Settings] Load failed: {ex.Message} - using defaults.");
                return new AppSettings();
            }
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(SettingsDir);
                var opts = new JsonSerializerOptions { WriteIndented = true };
                File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, opts));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Settings] Save failed: {ex.Message}");
            }
        }
    }
}
