using System;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace MoyuPopup.Core;

/// <summary>配置管理：加载/保存 config.json；损坏自动备份并回退默认；原子写入防损坏</summary>
public static class ConfigManager
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>当前生效配置</summary>
    public static AppConfig Config { get; private set; } = new();

    /// <summary>应用数据目录 %APPDATA%\MoyuPopup</summary>
    public static string AppDataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MoyuPopup");

    private static string FilePath => Path.Combine(AppDataDir, "config.json");

    /// <summary>加载配置；文件缺失则写入默认；损坏则备份为 .bad 并回退默认</summary>
    public static AppConfig Load()
    {
        Directory.CreateDirectory(AppDataDir);
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                Config = JsonSerializer.Deserialize<AppConfig>(json, JsonOpts) ?? new AppConfig();
            }
            else
            {
                Config = new AppConfig();
                Save();
            }
        }
        catch (Exception ex)
        {
            Log.Error("配置加载失败，回退默认", ex);
            try { File.Copy(FilePath, FilePath + ".bad", overwrite: true); } catch { /* 备份失败忽略 */ }
            Config = new AppConfig();
        }
        return Config;
    }

    /// <summary>原子保存：先写临时文件再替换，防止写入中断损坏配置</summary>
    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(AppDataDir);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Config, JsonOpts));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error("配置保存失败", ex);
        }
    }
}
