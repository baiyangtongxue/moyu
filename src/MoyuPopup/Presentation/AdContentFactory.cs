using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MoyuPopup.Core;

namespace MoyuPopup.Presentation;

/// <summary>广告素材工厂：首次运行程序化生成默认促销图；加载目录素材；提供内存兜底素材</summary>
public static class AdContentFactory
{
    private static readonly string[] ImageExts = { ".jpg", ".jpeg", ".png", ".bmp" };

    /// <summary>单张广告主题（标题/副标题/价格/渐变配色）</summary>
    private record AdTheme(string Headline, string Subline, string Price, string ColorTop, string ColorBottom);

    private static readonly AdTheme[] Themes =
    {
        new("限时特惠", "全场低至 1 折起", "¥9.9", "#FF3A7BD5", "#FF00D2FF"),
        new("新品首发", "爆款好物 抢先购", "¥199", "#FFFF512F", "#FFDD2476"),
        new("会员狂欢日", "开卡立省 ¥50", "¥0.01", "#FF7F00FF", "#FFE10098"),
        new("包邮风暴", "全场包邮 不满意包退", "¥0", "#FF11998E", "#FF38EF7D"),
        new("秒杀专场", "整点秒杀 手慢无", "¥1", "#FFFC466B", "#FF3F5EFB"),
        new("超级品牌日", "大牌直降 仅此一天", "¥999", "#FF141E30", "#FF243B55"),
    };

    /// <summary>确保默认素材存在：目录中无图片时程序化生成 6 张 800×450 促销图</summary>
    public static void EnsureDefaultAds(string dir)
    {
        Directory.CreateDirectory(dir);
        var hasImage = Directory.EnumerateFiles(dir)
            .Any(f => ImageExts.Contains(Path.GetExtension(f).ToLowerInvariant()));
        if (hasImage) return;

        for (var i = 0; i < Themes.Length; i++)
        {
            try
            {
                GeneratePng(Path.Combine(dir, $"ad_{i + 1:00}.png"), Themes[i]);
            }
            catch (Exception ex)
            {
                Log.Error($"生成默认广告 {i + 1} 失败", ex);
            }
        }
        Log.Info($"默认广告素材就绪: {dir}");
    }

    /// <summary>渲染主题为 800×450 PNG 并写入文件</summary>
    private static void GeneratePng(string path, AdTheme t)
    {
        var rtb = RenderTheme(t);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    /// <summary>加载目录下全部图片素材（按文件名排序；解码上限 800px 控制内存）</summary>
    public static List<ImageSource> LoadAds(string dir)
    {
        var list = new List<ImageSource>();
        if (!Directory.Exists(dir)) return list;

        var files = Directory.EnumerateFiles(dir)
            .Where(f => ImageExts.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);

        foreach (var f in files)
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = 800;
                bmp.UriSource = new Uri(f);
                bmp.EndInit();
                bmp.Freeze();          // 跨线程安全 + 免 UI 锁
                list.Add(bmp);
            }
            catch (Exception ex)
            {
                Log.Warn($"广告图片加载失败，已跳过: {f} ({ex.Message})");
            }
        }
        return list;
    }

    /// <summary>内存兜底素材：目录不可用时返回一张简单推广图（保证轮播永远有内容）</summary>
    public static ImageSource CreateFallbackSlide() => RenderTheme(Themes[0]);

    /// <summary>将主题绘制为 800×450 位图（渐变背景/装饰圆/标题/价格/假按钮/角标/免责行）</summary>
    private static RenderTargetBitmap RenderTheme(AdTheme t)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var c1 = (Color)ColorConverter.ConvertFromString(t.ColorTop);
            var c2 = (Color)ColorConverter.ConvertFromString(t.ColorBottom);

            // 背景渐变 + 装饰圆
            dc.DrawRectangle(new LinearGradientBrush(c1, c2, 90), null, new Rect(0, 0, 800, 450));
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF)), null, new Point(660, 90), 140, 140);
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF)), null, new Point(110, 390), 170, 170);

            // 主文案
            DrawText(dc, t.Headline, 64, true, Colors.White, new Point(56, 96));
            DrawText(dc, t.Subline, 26, false, Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF), new Point(60, 196));
            DrawText(dc, t.Price, 84, true, Color.FromRgb(0xFF, 0xE2, 0x4C), new Point(56, 250));

            // 假“立即抢购”按钮
            var btn = new Rect(56, 368, 176, 46);
            dc.DrawRoundedRectangle(Brushes.White, null, btn, 23, 23);
            DrawTextCentered(dc, "立即抢购", 20, true, c2, btn);

            // “广告”角标（右上）
            var badge = new Rect(712, 16, 72, 28);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0x88, 0, 0, 0)), null, badge, 4, 4);
            DrawTextCentered(dc, "广告", 14, false, Colors.White, badge);

            // 底部免责小字（增强真实感）
            DrawText(dc, "活动最终解释权归商家所有 | 图片仅供参考", 14, false,
                Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF), new Point(56, 420));
        }

        var rtb = new RenderTargetBitmap(800, 450, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    /// <summary>按左上角原点绘制文本</summary>
    private static void DrawText(DrawingContext dc, string text, double size, bool bold, Color color, Point origin)
        => dc.DrawText(CreateFormatted(text, size, bold, color), origin);

    /// <summary>在目标矩形内居中绘制文本</summary>
    private static void DrawTextCentered(DrawingContext dc, string text, double size, bool bold, Color color, Rect rect)
    {
        var ft = CreateFormatted(text, size, bold, color);
        dc.DrawText(ft, new Point(rect.X + (rect.Width - ft.Width) / 2, rect.Y + (rect.Height - ft.Height) / 2));
    }

    /// <summary>构造 FormattedText（微软雅黑，中文排版）</summary>
    private static FormattedText CreateFormatted(string text, double size, bool bold, Color color)
    {
        var tf = new Typeface(
            new FontFamily("Microsoft YaHei"),
            FontStyles.Normal,
            bold ? FontWeights.Bold : FontWeights.Normal,
            FontStretches.Normal);
        return new FormattedText(text, CultureInfo.GetCultureInfo("zh-CN"),
            FlowDirection.LeftToRight, tf, size, new SolidColorBrush(color), 1.0);
    }
}
