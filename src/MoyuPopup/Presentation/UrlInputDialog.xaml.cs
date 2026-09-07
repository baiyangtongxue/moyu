using System;
using System.Windows;
using System.Windows.Controls;

namespace MoyuPopup.Presentation;

/// <summary>「输入视频链接」对话框：预填剪贴板链接，回车/确定返回</summary>
public partial class UrlInputDialog : Window
{
    /// <summary>用户输入的视频链接（去除首尾空白）</summary>
    public string VideoUrl => UrlBox.Text.Trim();

    /// <summary>构造：若剪贴板内容是链接则预填并全选</summary>
    public UrlInputDialog()
    {
        InitializeComponent();
        try
        {
            var t = Clipboard.GetText();
            if (t.StartsWith("http", StringComparison.OrdinalIgnoreCase)) UrlBox.Text = t;
        }
        catch { /* 剪贴板不可用时忽略 */ }
        Loaded += (s, e) => { UrlBox.Focus(); UrlBox.SelectAll(); };
    }

    /// <summary>确定：空输入不放行，聚焦输入框提示</summary>
    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(UrlBox.Text))
        {
            UrlBox.Focus();
            return;
        }
        DialogResult = true;
    }
}
