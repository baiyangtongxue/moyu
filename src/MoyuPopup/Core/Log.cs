using System;
using System.IO;

namespace MoyuPopup.Core;

/// <summary>极简滚动日志：按天写文件，自动清理过期日志（M2 可替换为 Serilog）</summary>
public static class Log
{
    private static readonly object Lock = new();
    private static string? _dir;
    private const int KeptDays = 7;

    /// <summary>初始化日志目录并清理超过保留期的旧日志</summary>
    public static void Init(string dir)
    {
        _dir = dir;
        try
        {
            Directory.CreateDirectory(dir);
            var cutoff = DateTime.Now.AddDays(-KeptDays);
            foreach (var f in Directory.EnumerateFiles(dir, "app-*.log"))
            {
                if (File.GetLastWriteTime(f) < cutoff) File.Delete(f);
            }
        }
        catch { /* 日志初始化失败不影响运行 */ }
        Info("---- Log.Init ----");
    }

    /// <summary>记录信息级日志</summary>
    public static void Info(string msg) => Write("INFO", msg);

    /// <summary>记录警告级日志</summary>
    public static void Warn(string msg) => Write("WARN", msg);

    /// <summary>记录错误级日志（含异常详情）</summary>
    public static void Error(string msg, Exception? ex = null) => Write("ERROR", ex == null ? msg : $"{msg} | {ex}");

    /// <summary>按日期滚动写入一行日志（线程安全，写失败静默忽略）</summary>
    private static void Write(string level, string msg)
    {
        if (_dir == null) return;
        try
        {
            lock (Lock)
            {
                var path = Path.Combine(_dir, $"app-{DateTime.Now:yyyyMMdd}.log");
                File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} [{level}] {msg}{Environment.NewLine}");
            }
        }
        catch { /* 忽略写日志异常 */ }
    }
}
