# 实际测试记录

## 2026-11-26 媒体库第一阶段（数据打通）

- `dotnet build -c Release` 通过，无警告、无错误；71 项核心测试通过（新增 10 项）。覆盖：21 种发布命名的作品名、季度、集数与特别篇 / 花絮识别；季度与特典文件夹；订阅目录、季度文件夹、合集文件夹与散落文件分组；重新扫描时保留资料和首次入库时间；订阅身份变化时重置自动匹配但保留手动匹配；订阅目录认领规则；Bangumi 条目、分集与搜索解析，续作与无关结果的挑选；海报文件头校验；扫描跳过下载中、隐藏和 NAS 系统目录；索引损坏时另存并重建；下载完成时沿磁力父任务记录订阅来源。
- `python tests/integration.py` 通过：模拟的 Mikan 页面与 Bangumi API 覆盖订阅目录经 Mikan 链接匹配、分集标题、回写订阅 `bgmId`、搜索兜底、拒收 SVG 海报、海报缓存头、手动匹配与失败、不存在作品和非法 ID 返回 404、设置校验、关闭后拒绝扫描、所有 Bangumi 请求带项目 User-Agent。
- `node tests/browser/smoke.cjs` 通过：番剧页订阅时保存 Mikan ID；设置页媒体库分区的保存、立即扫描、状态显示与刷新后恢复；无 JS 错误。另截图检查了桌面和 390px 手机布局。
- `integration.py --real-aria`、`real_downloads.py`（含真实任务完成后的后台监控）、`notifications.py`、`local_tuning.py`、`mikan_fallback.py` 通过。
- 一次性只读联网检查（未加入测试集）：用真实 Mikan 与 Bangumi、按真实发布命名的空文件，5 部作品全部匹配正确，包括 Mikan 链接、罗马字名搜索、繁体名第二季、合集文件夹第二季；海报为 140–170 KB 的 JPEG。该检查发现繁体第二季曾被匹配到第一季，已修正并补充单元测试。
- 未在真实 NAS（Synology / 群晖目录结构）或大量文件（数千集）的目录上测试扫描耗时。

## 2026-11-25 多下载源搜索与通用种子预取

- `dotnet build -c Release` 通过，无警告、无错误；61 项核心测试通过（新增 15 项），覆盖 Nyaa / DMHY / ACG.RIP / Torznab / Kisssub 解析、大小换算、旧订阅去重键不变、Torznab 错误文档、API Key 注入与隐藏、搜索地址与镜像路径、预取匹配规则和配置校验。
- `python tests/integration.py` 通过：多源并行搜索与单源失败隔离（模拟 503 人机验证）、分类透传与非法分类拒绝、Torznab 能力测试与错误 Key、订阅地址不含 API Key 但预览和检查可用、下载链接 302 跳转磁力链、合法种子 / 跳转链 / HTML 错误页 / 无法连接时交由节点 / 关闭开关、备份不含 API Key。
- `python tests/mikan_fallback.py`（真实 aria2）全部通过，Mikan 主备站点切换与旧失败任务重试行为不变；`integration.py --real-aria`、`local_tuning.py`、`notifications.py`、`real_downloads.py` 通过。
- `node tests/browser/smoke.cjs` 通过：日历 / 搜索切换、结果合并与来源标记、未知大小显示「—」、一键订阅与下载、详情页跨源搜索、设置保存与 Torznab 测试、390px 手机无横向溢出，无 JS 错误。
- 未连接真实 ACG.RIP、Nyaa、动漫花园、末日动漫或 Prowlarr / Jackett；各站真实 RSS 结构以调研为准，站点改版后可能需要调整。

## 2026-10-13 Bark 与独立通知开关

- `dotnet build -c Release` 通过，无警告、无错误；46 项核心测试通过。
- `python tests/notifications.py` 使用本地模拟接口验证两种推送的请求格式、Bark 服务地址自动补全（有无末尾斜杠及完整 `/push` 地址）、缺少 Key、服务端错误、非法 Bark 地址、四种开关组合，以及失败渠道重试时成功渠道不重复发送。
- `python tests/integration.py` 与 `node tests/browser/smoke.cjs` 通过；浏览器覆盖两个测试按钮、开关独立保存与刷新恢复、桌面及手机布局，无 JS 错误。
- 未使用真实 PushDeer / Bark 设备凭据验证手机收件。

## 2026-10-05 弹窗与番剧卡片回归

- 下载与订阅弹窗共用目录选择器，空值显示当前节点默认路径；未配置远程默认目录时查询真实 RPC 的 dir。历史保存在当前浏览器，按节点分别保留最近 20 个成功使用的自定义目录。
- 订阅最终目录按「基础目录 / 作品目录 / AI 识别名 / Season N」顺序实时展示；AI 尚未识别时展示占位说明，作品目录采用与服务端一致的非法字符处理。
- 订阅预览使用独立模态窗口；返回、关闭或 Escape 后原表单保持不变；预览匹配同时应用包含与排除规则。
- 卡片封面固定比例、顶部对齐，标题固定两行并省略，移除标题下说明。可见卡片按需加载标记，服务端限制四路并发并缓存十分钟。
- Hot 按原桌面阈值：5–6 个字幕组为粉色，7–100 个为紫色；更新数为最近 24 小时有首条发布的字幕组数；最新集数为各字幕组首条发布解析所得集数的最大值。复用原项目集数提取工具，不把首页资源数量当成集数。
- 19 项核心测试、接口测试、桌面/390px 手机浏览器回归通过，覆盖目录跨弹窗复用、刷新持久化、节点历史隔离、预览返回状态、两行标题、Hot 和集数呈现。真实 Mikan 抽样解析通过。

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
