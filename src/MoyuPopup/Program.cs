using System;
using System.Windows;
using Velopack;

namespace MoyuPopup;

/// <summary>
/// 自定义入口：先运行 Velopack 钩子（安装/更新/首次启动等），再启动 WPF。
/// 默认 App.xaml 生成的 Main 被 StartupObject 覆盖（M5 打包自动更新用）。
/// </summary>
public static class Program
{
    /// <summary>应用入口（STA 线程）</summary>
    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build().Run();
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
