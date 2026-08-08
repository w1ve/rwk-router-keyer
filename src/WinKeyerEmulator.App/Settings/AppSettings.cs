using System.Text.Json;

namespace WinKeyerEmulator.App.Settings;

public class AppSettings
{
    public string? KeyingPortName { get; set; }
    public string KeyingLine { get; set; } = "DTR";
    public string? CommandPortName { get; set; }
    public string UdpAddress { get; set; } = "127.0.0.1";
    public int UdpPort { get; set; } = 7388;
    public bool LogRawData { get; set; }

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WKRServer", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch { }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch { }
    }
}
