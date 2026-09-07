using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace MoyuPopup.Presentation;

/// <summary>
/// 广告轮播控件：图片淡入淡出轮播 + 假关闭按钮/进度点/假倒计时。
/// 纯 WPF 渲染，不联网（设计书 5.3）。
/// </summary>
public sealed class AdCarouselControl : Grid
{
    private readonly Image _image = new()
    {
        Stretch = Stretch.UniformToFill,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Stretch,
    };

    private readonly Border _closeButton;
    private readonly StackPanel _dots = new()
    {
        Orientation = Orientation.Horizontal,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Bottom,
        Margin = new Thickness(0, 0, 0, 10),
    };
    private readonly Border _countdownBox;
    private readonly TextBlock _countdownText = new() { FontSize = 12, Foreground = Brushes.White };

    private readonly List<ImageSource> _slides = new();
    private readonly List<Ellipse> _dotItems = new();
    private readonly DispatcherTimer _rotateTimer;
    private readonly DispatcherTimer _countdownTimer;
    private readonly Random _rand = new();

    private int _index = -1;
    private int _countdownSec;
    private int _intervalSec = 5;
    private bool _shuffle = true;
    private bool _animating;
    private bool _started;

    /// <summary>假关闭按钮被点击（等效老板键，符合广告弹窗肌肉记忆）</summary>
    public event Action? CloseRequested;

    /// <summary>构建视觉树（图片层 + 角标 + 关闭钮 + 倒计时 + 进度点）与两个定时器</summary>
    public AdCarouselControl()
    {
        Children.Add(_image);

        // “广告”角标（左上）
        var badge = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x66, 0, 0, 0)),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(6, 2, 6, 2),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(8),
            Child = new TextBlock { Text = "广告", FontSize = 11, Foreground = Brushes.White },
            IsHitTestVisible = false,
        };
        Children.Add(badge);

        // 假关闭按钮（右上）：点击 = 老板键
        _closeButton = new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(11),
            Background = new SolidColorBrush(Color.FromArgb(0x44, 0, 0, 0)),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(6),
            Cursor = Cursors.Hand,
            Child = new TextBlock
            {
                Text = "✕",
                FontSize = 11,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        _closeButton.MouseEnter += (s, e) => _closeButton.Background = new SolidColorBrush(Color.FromArgb(0x88, 0, 0, 0));
        _closeButton.MouseLeave += (s, e) => _closeButton.Background = new SolidColorBrush(Color.FromArgb(0x44, 0, 0, 0));
        _closeButton.MouseLeftButtonUp += (s, e) => CloseRequested?.Invoke();
        Children.Add(_closeButton);

        // 假倒计时（右下）
        _countdownBox = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x66, 0, 0, 0)),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(5, 2, 5, 2),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 10, 10),
            Child = _countdownText,
            IsHitTestVisible = false,
        };
        Children.Add(_countdownBox);

        // 进度点（底部居中）
        Children.Add(_dots);

        _rotateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(_intervalSec) };
        _rotateTimer.Tick += async (s, e) => await RotateToNextAsync();

        _countdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _countdownTimer.Tick += (s, e) =>
        {
            if (_countdownSec > 5)
            {
                _countdownSec--;
                RenderCountdown();
            }
        };
    }

    /// <summary>装载轮播素材与参数</summary>
    public void LoadSlides(IEnumerable<ImageSource> slides, int intervalSec, bool shuffle)
    {
        _slides.Clear();
        _slides.AddRange(slides.Where(s => s != null));
        _intervalSec = Math.Max(2, intervalSec);
        _shuffle = shuffle;
        _rotateTimer.Interval = TimeSpan.FromSeconds(_intervalSec);
        RebuildDots();
    }

    /// <summary>运行时更新轮播参数（设置窗口保存后调用；素材保持不变）</summary>
    public void UpdateSchedule(int intervalSec, bool shuffle)
    {
        _intervalSec = Math.Max(2, intervalSec);
        _shuffle = shuffle;
        _rotateTimer.Interval = TimeSpan.FromSeconds(_intervalSec);
    }

    /// <summary>开始轮播（立即显示第一张并启动定时器）</summary>
    public void Start()
    {
        _started = true;
        if (_slides.Count > 0)
        {
            if (_index < 0)
            {
                _index = 0;
                _image.Source = _slides[0];
                UpdateDots();
                ResetCountdown();
            }
        }
        else
        {
            _image.Source = AdContentFactory.CreateFallbackSlide();
        }
        _rotateTimer.Start();
        _countdownTimer.Start();
    }

    /// <summary>暂停轮播（播放态下由窗口调用）</summary>
    public void Pause()
    {
        _rotateTimer.Stop();
        _countdownTimer.Stop();
    }

    /// <summary>恢复轮播</summary>
    public void Resume()
    {
        if (!_started) return;
        _rotateTimer.Start();
        _countdownTimer.Start();
    }

    /// <summary>淡出 → 换图 → 淡入，切换到下一张</summary>
    private async Task RotateToNextAsync()
    {
        if (_slides.Count == 0 || _animating) return;
        _animating = true;
        try
        {
            _index = PickNext();
            await FadeAsync(_image, 0, 250);
            _image.Source = _slides[_index];
            await FadeAsync(_image, 1, 250);
            UpdateDots();
            ResetCountdown();
        }
        finally
        {
            _animating = false;
        }
    }

    /// <summary>选择下一张索引：随机不重复或顺序循环</summary>
    private int PickNext()
    {
        if (_slides.Count == 1) return 0;
        if (_shuffle)
        {
            int n;
            do { n = _rand.Next(_slides.Count); } while (n == _index);
            return n;
        }
        return (_index + 1) % _slides.Count;
    }

    /// <summary>对元素透明度做动画并等待完成</summary>
    private static Task FadeAsync(UIElement el, double to, int ms)
    {
        var tcs = new TaskCompletionSource();
        var anim = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { FillBehavior = FillBehavior.Stop };
        anim.Completed += (s, e) => tcs.TrySetResult();
        el.BeginAnimation(OpacityProperty, anim);
        return tcs.Task;
    }

    /// <summary>重置当前张的假倒计时（59~179 秒）</summary>
    private void ResetCountdown()
    {
        _countdownSec = _rand.Next(59, 180);
        RenderCountdown();
        _countdownTimer.Stop();
        _countdownTimer.Start();
    }

    /// <summary>渲染倒计时文本（mm:ss）</summary>
    private void RenderCountdown() => _countdownText.Text = $"距结束 {_countdownSec / 60:00}:{_countdownSec % 60:00}";

    /// <summary>按素材数量重建进度点</summary>
    private void RebuildDots()
    {
        _dots.Children.Clear();
        _dotItems.Clear();
        for (var i = 0; i < _slides.Count; i++)
        {
            var dot = new Ellipse
            {
                Width = 7,
                Height = 7,
                Margin = new Thickness(3, 0, 3, 0),
                Fill = Brushes.White,
                Opacity = 0.35,
            };
            _dotItems.Add(dot);
            _dots.Children.Add(dot);
        }
        UpdateDots();
    }

    /// <summary>高亮当前进度点</summary>
    private void UpdateDots()
    {
        for (var i = 0; i < _dotItems.Count; i++)
        {
            _dotItems[i].Opacity = i == _index ? 1 : 0.35;
        }
    }
}
