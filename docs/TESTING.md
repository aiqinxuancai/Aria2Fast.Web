# 实际测试记录

测试日期：2026-10-03。系统：Windows；Web 为 .NET 10 Release、自包含 win-x64 发布；下载引擎为 GitHub 官方 aria2 1.37.0。

## 已通过

- 17 项 C# 核心测试、HTTP fixture 集成测试、Chromium 五页面回归测试。
- 从独立工作目录启动 Windows 自包含程序，并调用捆绑引擎完成真实下载。
- 94,208 字节自有测试内容的 SHA256 为 e1aa857c8b6a2cffcf368085bd95feecda6e746af5d09933587c0c1b188c2f88；下载到磁盘及浏览器取回后的内容均一致。
- 暂停任务的 GID、状态和全局限速/并发数在本地引擎重启后恢复。
- 已完成文件的认证访问、HTTP Range、中文改名与旧路径失效。
- 自建 Torrent 上传、选文件、暂停、引擎重启恢复及移除；该项不验证公网 BT 传输。
- 自建 Metalink 上传、HTTP 下载和引擎 SHA256 校验。
- 远程节点接口连接真实本机引擎、错误密钥拒绝、后台完成记录；未测试异地网络。
- Chromium 390×844 手机模拟触摸视口：界面提交任务、完成显示、文件取回和校验；1440×1000 桌面截图；系统及自选主题、刷新后持久化，无脚本错误。
- 真实 Mikan 源只读访问：2026 年秋季列表 29 部，抽样详情 12 个字幕组，RSS 预览 100 条；不触发内容下载。

实际测试发现 aria2 不接受原 HTTP 客户端生成的 chunked JSON-RPC 请求；已改为序列化后用 StringContent 发送，显式产生 Content-Length。fixture 同步拒绝分块 RPC 请求，覆盖此回归。

## 复现（PowerShell，项目根目录）

    dotnet build -c Release
    dotnet run --project tests/Aria2Fast.Web.Tests -c Release --no-build
    dotnet publish src/Aria2Fast.Web -c Release -r win-x64 --self-contained true -o artifacts/win-x64-check
    ./scripts/install-aria2.ps1 -Destination artifacts/win-x64-check/aria2
    $env:TEST_PUBLISH_DIR=(Resolve-Path artifacts/win-x64-check).Path
    $env:PYTHONUTF8='1'
    python tests/integration.py
    python tests/integration.py --real-aria
    python tests/real_downloads.py
    npm --prefix tests/browser install
    Push-Location tests/browser
    npx playwright install chromium
    Pop-Location
    node tests/browser/smoke.cjs
    node tests/browser/real.cjs
    python tests/live_mikan.py

真实下载脚本使用临时数据目录、随机 Web/RPC 端口和本地自有 HTTP 内容，不读取桌面配置。Mikan 检查为可选联网测试，不加入离线 CI。未设置 TEST_PUBLISH_DIR 时普通测试使用本项目构建输出；真实测试仍需可用的 aria2。

## 证据与限制

本机生成的报告保存在 artifacts/real-download-results.json、artifacts/real-browser-results.json、artifacts/live-mikan-results.json，截图在 artifacts/screenshots/。artifacts 不纳入源码版本管理；Actions 会上传相应测试证据。

Windows 自包含发布及启动已通过。Docker/macOS 未在本机运行，GitHub Actions 未远程执行，GHCR/Release 未实际发布。手机验证使用 Chromium 模拟器，未使用实体 iOS/Android。未验证公网 BT/FTP、真实付费 AI、TMDB/Tavily、OSS 或 PushDeer 服务凭据端到端调用。测试通过范围不等同于这些外部环境已获验证。
