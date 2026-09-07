namespace MoyuPopup.Core;

/// <summary>一个待播放的视频条目（M3 起持久化进 playlist.json，字段对应设计书 8.3）</summary>
public sealed class VideoItem
{
    /// <summary>稳定 ID（EmbedUrl 归一化哈希，去重与位置记忆的键）</summary>
    public string Id { get; set; } = "";

    /// <summary>平台标识（bilibili / tencent / custom）</summary>
    public string Platform { get; set; } = "";

    /// <summary>展示标题</summary>
    public string Title { get; set; } = "";

    /// <summary>用户输入的原始链接</summary>
    public string InputUrl { get; set; } = "";

    /// <summary>WebView2 实际加载的嵌入地址</summary>
    public string EmbedUrl { get; set; } = "";

    /// <summary>分类标签（M4 设置界面编辑）</summary>
    public string[] Tags { get; set; } = Array.Empty<string>();

    /// <summary>入队时间</summary>
    public DateTime AddedAt { get; set; }

    /// <summary>上次播放位置（秒；&gt;30s 才记录，切回续播）</summary>
    public int LastPositionSec { get; set; }

    /// <summary>视频时长（秒，可选）</summary>
    public int? DurationSec { get; set; }
}

/// <summary>
/// 视频源适配器：把用户输入的链接解析为可播放的嵌入地址（设计书 7.1）。
/// 新增平台 = 新增实现类并在配置 sources.enabled 中登记，核心零改动。
/// </summary>
public interface IVideoSourceAdapter
{
    /// <summary>平台标识</summary>
    string Platform { get; }

    /// <summary>判断该适配器能否处理此 URL</summary>
    bool CanParse(string url);

    /// <summary>解析输入链接（普通页/分享短链/纯 ID）为 VideoItem；失败抛异常</summary>
    Task<VideoItem> ParseAsync(string url, CancellationToken ct);

    /// <summary>WebView2 环境附加参数（空表示无特殊要求）</summary>
    IEnumerable<string> BrowserArgs => Array.Empty<string>();

    /// <summary>平台专属“播放”JS（空串表示使用通用 video 元素控制）</summary>
    string BuildPlayJs(VideoItem item) => "";

    /// <summary>平台专属“暂停”JS（空串表示使用通用 video 元素控制）</summary>
    string BuildPauseJs(VideoItem item) => "";
}
