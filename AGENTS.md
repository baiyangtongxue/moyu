# AGENTS.md

"摸鱼" stealth video popup player: a .NET 8 **WPF** desktop app (Windows-only) that masquerades as a bottom-right ad popup (`Ad` state) but plays top-up videos (B站/腾讯/抖音/爱奇艺/web) on hover. **All code comments, config keys, log messages, and UI strings are Simplified Chinese** — match the convention and use `///` XML doc comments (present on nearly every member).

## Build & run

- Single project, **no solution file**: `dotnet build src/MoyuPopup/MoyuPopup.csproj`
- `net8.0-windows` — only builds on Windows. Requires the **.NET 8 SDK** (`dotnet --list-sdks` should show an 8.0/9.0 SDK).
- **No tests, no CI, no formatter/linter, no `.editorconfig`, no `global.json`.** There is no test runner to invoke — verification is manual against the design doc's acceptance checklist (`摸鱼视频弹窗播放器-详细设计说明书.md` §13.4).
- `ImplicitUsings` is on, but the `System.Drawing` / `System.Windows.Forms` using directives are **explicitly removed** in the csproj (``<Using Remove=...>``) to avoid WPF type clashes (`Application`/`Color`/`Point`/`Image`). Do not re-add them.
- **Packaging (M5) uses Velopack**: the app has a custom entry `Program.Main` (`StartupObject=MoyuPopup.Program`) that calls `VelopackApp.Build().Run()` before WPF. To build an installer:
  `dotnet publish src/MoyuPopup/MoyuPopup.csproj -c Release -r win-x64 --self-contained true -o publish`
  then `vpk pack --packId AdPopup --packVersion <ver> --packDir publish --mainExe AdPopup.exe --packTitle "广告弹窗投放" --outputDir Releases` (`vpk` is a global tool: `dotnet tool install -g vpk`). Output: `Releases\AdPopup-win-Setup.exe` + `AdPopup-win-Portable.zip`. Target machines still need the WebView2 Runtime (lazy-init, message if missing).

## Runtime prerequisites

- **WebView2 Runtime** must be installed; it is lazy-initialized on first play and shows a message if missing. Design target: Windows 10 1809+.
- Spec file and some repo filenames contain Chinese; the shell garbles them — use `glob` to find them instead of typing paths.

## Architecture

- `Core/` = all logic (`AppConfig`, `AppStateMachine`, `ConfigManager`, `PlaylistManager`, `HotkeyManager`, `MouseWatcher`, `SingleInstanceGuard`, `AutoStartManager`, `VideoSource`/`VideoSourceAdapters`, `Log`); `Presentation/` = WPF UI (`PopupWindow`, `PlaybackController`, `PlayerHost`, `TrayController`, settings/login/playlist windows). `Core` holds no UI reference; keep it that way.
- Lifecycle in `App.xaml.cs` `OnStartup`: single-instance → log init → config → default ads → state machine → `PopupWindow` → tray. `ShutdownMode="OnExplicitShutdown"` — the app only exits via tray exit, **never** by closing the window.
- State machine: `Hidden` / `Ad` / `Playing`. `Ad→Playing` requires hover ≥ `hoverDelayMs` AND `lockAdMode=false`. `Restore` **always → `Ad`** (never to Playing, by design). Next/Prev hotkeys work from `Ad` too.
- `MouseWatcher` deliberately uses **100ms polling + Win32** (`GetCursorPos`/`GetWindowRect`/`WindowFromPoint`), not WPF mouse events — WebView2/borderless-window mouse events are unreliable. Do not "fix" it back to event-based.
- All WebView2 controls (player, login window, login session) share **one** `CoreWebView2Environment` via `Presentation/WebView2EnvironmentProvider` over the single user-data dir `%APPDATA%\MoyuPopup\WebView2`. Do **not** create separate environments on the same folder — WebView2 forbids multiple environments per user-data folder (they'd fail with "user data folder in use"). The provider also carries the `--autoplay-policy=no-user-gesture-required` arg used by the player.

## Video sources

- `IVideoSourceAdapter` (`VideoSource.cs`) = turn one input link into an embed URL. New platform = add an adapter class **and** register it in `VideoSourceRouter.Adapters` plus its tag in `sources.enabled`. `custom` is always appended last as the fallback. No core changes.
- B站/腾讯 resolve to **official embed iframe** players; 抖音/爱奇艺 load the page and inject CSS (`PlatformAdaptJs`). These page-based adapters are version-sensitive and break on site redesigns — keep each adapter isolated so a failure only affects that platform.
- Shared `BilibiliHttpClient` (browser UA, 8s timeout) is reused for short-link redirect following (b23.tv / v.douyin.com). Use it rather than new `HttpClient`s.
- **List fetching** (获取播放列表/随机视频) uses `IPlatformListSource` / `IPlatformSession` / `ListSourceRegistry` (`Core/IPlatformListSource.cs`). New platform = new `IPlatformListSource` impl **and** register it in `ListSourceRegistry.Sources`; the `PlaylistWindow` platform/category dropdowns are built automatically from the registry. Currently only `BilibiliListSource` is registered: **随机视频** (免登录, B站 `x/web-interface/popular`, random-sampled) + 观看历史/稍后再看 (需 `SESSDATA`, B站 `history/cursor` + `history/toview/web`). Douyin/tencent have no clean public list API (`X-Bogus` signature / private endpoints) — leave them login-only.

## Data & config

- Everything under `%APPDATA%\MoyuPopup\`: `config.json`, `playlist.json`, `history.json`, `ads/`, `WebView2/`, `logs/`.
- JSON is CamelCase, written **atomically** (write `*.tmp`, then move); corrupt files are backed up as `*.bad` and defaults fall back. Playlist dedup key = SHA256(embedUrl) first 16 hex chars.
- Logs are daily `app-yyyyMMdd.log`, 7-day retention. `Log.*` swallows failures by design.

## Stealth naming (deliberate — do not "clean up")

- Assembly/Product/exe + registry `Run` value = **`AdPopup`**; namespace/root = **`MoyuPopup`**. Tray tooltip "广告服务", default window title "广告推广". This dual naming is intentional disguise; keep it consistent.
- Design constraint (§11): **no** process injection, **no** keyboard hooking (only Win32 `RegisterHotKey`), **no** anti-detection, zero network telemetry. Stay within this boundary.
- Global hotkeys default to **symbol keys** — next/prev = `Ctrl+Alt+.`/`Ctrl+Alt+,`, volume = `Ctrl+Alt+=`/`Ctrl+Alt+-`. **Never set** global hotkeys to `Ctrl+Alt+Arrow` (or similar) — they conflict with the Intel/NVIDIA **display-rotation** system shortcut, which pops a rotation prompt + steals focus and misplaces the popup window, breaking hover-to-show (video keeps playing hidden → "无画面却在播放").
