using System;
using System.Threading;

namespace MoyuPopup.Core;

/// <summary>单实例守护：命名互斥体（会话级，同会话仅允许一个实例）</summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private const string MutexName = @"Local\MoyuPopup_SingleInstance";
    private Mutex? _mutex;
    private bool _owned;

    /// <summary>尝试获取互斥体；false 表示已有实例在运行</summary>
    public bool TryAcquire()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out _owned);
        return _owned;
    }

    /// <summary>释放互斥体</summary>
    public void Dispose()
    {
        if (_mutex == null) return;
        try { if (_owned) _mutex.ReleaseMutex(); } catch { /* 已释放则忽略 */ }
        _mutex.Dispose();
        _mutex = null;
    }
}
