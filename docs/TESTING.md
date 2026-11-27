# 实际测试记录

## 2026-11-26 媒体库首页改版与 README 截图更新

- 只改了前端、测试与文档，后端代码未变动，本次未重新运行核心测试与 `tests/integration.py`。
- `node tests/browser/smoke.cjs`（带与不带 ffmpeg 各一次）通过，无 JS 错误。新增与改写的检查：首页只显示「我的媒体」与「最近添加的番剧」两栏（没有观看记录时不出现继续观看 / 接下来），番剧入口显示作品数；首页搜索无结果时的提示；从「全部作品」进入作品页后返回链接回到该列表；看过第 1 集后首页「接下来」出现第 2 集；点「电影」入口进入电影列表，分类切换显示当前类别；栏目标题进入对应类别，再切换到「全部」；390px 手机上首页没有横向溢出。
- 调试中发现：电影目录扫描会让番剧重新获取资料，短时间内显示文件夹名，测试改为按名称前缀匹配并核对作品数；分类切换最初用了 `<nav>` 元素，被侧边栏的全局 `nav a` 样式影响（选中项出现描边），已改为普通容器。
- `node tests/browser/readme-screenshots.cjs` 重新生成全部 README 截图，新增媒体库首页、作品详情与播放器三张，手机拼图换成下载、媒体库首页与番剧日历。逐张查看了桌面深浅色、作品列表、无背景图与无海报时的继续观看卡片（测试截图 `library-wall.png`）。播放器截图的视频用 ffmpeg 8.1 生成。
- 未验证：Safari / iOS 上横向滚动栏目的回弹与吸附效果；作品很多（数百部）时首页的渲染速度。

## 2026-11-26 AI 刮削增强（DeepSeek、识图）与 NFO 写入

- `dotnet build -c Release` 通过，无警告、无错误；99 项核心测试通过（新增 6 项）。覆盖：DeepSeek 搜索与 Fetch 的请求格式（Bearer Key、沿用 DeepSeek AI 接口的 Key、检索词截到 128 字、`summary` 作为正文、Markdown 中的图片）；直接读取网页时提取 og:image 与 img（含懒加载、相对地址，排除 data:）；四种 AI 协议的图片输入格式，无图片时请求体不变；agent 查看图片（只能看工具结果中出现过的图片、图片随下一轮发送、笔记保留、无识图模型时不提供该工具）与结论中的类型、背景图、每集信息校验；Bangumi 标签与制作公司、分集简介，TMDB 类型、制作公司与背景图；NFO 写入（作品文件夹的 tvshow.nfo、海报、背景图与季海报，季文件夹的 season.nfo，每集 NFO 的标题、季、集、日期、简介与 XML 转义，跳过小数集数，电影文件夹与散落电影，资料更新后重写，不覆盖没有标记的 NFO 与已有图片）。
- `python tests/integration.py` 通过：第一阶段经 Mikan 匹配的作品在下载目录中写出 tvshow.nfo（含 Bangumi ID、简介）、PNG 海报与季海报、season.nfo，以及带分集标题和日期的每集 NFO；AI 刮削匹配的剧集写出 tvshow.nfo 与每集 NFO，AI 整理的电影写出 movie.nfo；网页读取方式设置校验。带 ffmpeg 的转码测试、`--real-aria`、`notifications.py` 同样通过。
- `node tests/browser/smoke.cjs`（带与不带 ffmpeg）发现新增的较长下拉选项让 390px 手机上的设置页横向溢出，已让表单列可收缩并限制下拉框宽度，修复后通过，无 JS 错误。
- 未验证：没有 DeepSeek Key，未真实调用 DeepSeek 搜索与 Fetch；没有使用真实的识图模型；未在真实 Jellyfin / Emby / Kodi 中导入生成的 NFO（格式按 Kodi NFO 规范编写）。

## 2026-11-26 媒体库分类、AI 刮削与 Keenable 搜索

- `dotnet build -c Release` 通过，无警告、无错误；93 项核心测试通过（新增 7 项）。覆盖：电影名与年份解析（含片名中带年份数字的情况）；媒体目录解析（下载目录内的相对路径、外部目录的 `@ID` 路径、嵌套目录排除、越界路径拒绝、已删除文件返回 404 而非符号链接错误、设置校验与 ID 生成）；按目录分类扫描（番剧根目录跳过 tvshows / movies、剧集季度与集数、电影文件夹的分段与花絮、散落电影按片名与年份区分、外部目录暂时缺失时保留）；TMDB 电影与单季解析、Bangumi 条目类型；AI 刮削 agent（只接受查到过的条目 ID、拒绝未出现过的网址、无效 JSON 与不可用工具、步数用尽、无法确定）；Keenable 免费接口与带 Key 接口的请求格式、`snippet` 取代空的 `description`、五个搜索服务的回退顺序与关闭搜索。
- `python tests/integration.py` 通过：默认三个媒体目录、重复目录与非法类别被拒绝、添加下载目录之外的电影目录；剧集与电影的分类、季度与年份；外部目录的视频可以分段读取；未配置 AI 时拒绝刮削；模拟 AI 按文件夹分别走通「搜索 Bangumi 后确认条目」「整理网页资料」「无法确定」三种结论，以及常规匹配失败后的自动刮削（原因写入作品错误信息，重新扫描后结果保留）；搜索服务设置校验。带 ffmpeg 的转码测试、`--real-aria`、`notifications.py` 同样通过。
- `node tests/browser/smoke.cjs`（带与不带 ffmpeg 各一次）通过：设置页默认三个媒体目录、添加目录保存后刷新仍在并获得 ID、删除目录、默认搜索服务为 Keenable；默认电影目录中的电影出现在「电影」分页，卡片显示年份，详情页为「视频文件」且没有「集数」按钮，播放页标题为电影名。无 JS 错误。
- 一次性联网检查（未加入测试集）：Keenable 免费接口（只带应用名请求头）返回结果，`description` 为空、正文在 `snippet` 中，与实现一致。
- 海报锯齿：对比同一张 Bangumi 封面的 800px 与 400px 版本在 176px 宽卡片上的渲染并放大检查，圆角边缘的抗锯齿正常，锯齿来自大幅缩小；改用 400px 版本并去掉悬停放大后更平滑。
- 未验证：真实 AI 模型的刮削效果（测试使用脚本化的模拟模型），真实 TMDB（没有测试用的 Key），Keenable 带 Key 接口与频率限制。

## 2026-11-26 媒体库第三阶段（ffmpeg 转封装 / 转码）

- 测试用的 ffmpeg 为 FFmpeg-Builds 发布的 Windows 版 8.1（`ffmpeg-n8.1-latest-win64-gpl`），放在临时目录，通过 `TEST_FFMPEG` 指定，未安装到系统。没有 ffmpeg 时相关测试会跳过；CI 会先安装发行版的 ffmpeg。
- `dotnet build -c Release` 通过，无警告、无错误；86 项核心测试通过（新增 6 项）。覆盖：ffprobe 结果解析（跳过封面图、HEVC Main 10 位深、多音轨、ASS / SRT / PGS 字幕分类与字体附件）；H.264、HEVC、AV1、VP9 与音频的 RFC 6381 编码名；关键帧解析与分段边界（转封装每个 GOP 一段、转码每 4 秒一段）；HLS 播放列表；转封装、转码、从中间开始、烧录 ASS / 图形字幕、去隔行、VAAPI / NVENC / QSV 的 ffmpeg 参数；ffmpeg 查找顺序；转码设置校验。
- `python tests/integration.py`（带 ffmpeg）通过：用 ffmpeg 生成 H.264 + AAC + 内封 ASS + 字体、HEVC 10bit + FLAC、VP9 + FLAC 三个测试视频；检测与测试编码器、媒体信息、内封 ASS 转 WebVTT（顶部对齐与换行）；转封装会话直接请求第 4 段时从该处重启 ffmpeg，读取分段内的 `tfdt` 确认时间戳为 8.0 秒，与播放列表一致，第 0 段为 0 秒；HEVC 10bit 转码为 480p 的 H.264，直接请求中间分段的时间戳同样正确；HEVC 转封装使用 Safari 需要的 hvc1；烧录内封 ASS；非法参数、结束会话后 404、关闭转码后不可用。
- GitHub Actions（Ubuntu 自带的 ffmpeg 6.1）发现：较旧的 ffmpeg 生成测试视频时，会按 AAC 编码延迟把整个文件的时间轴后移约 20 毫秒（字幕和画面同时后移，播放仍同步），使写死精确时间的断言失败。已改为允许这一偏移，并按实际时长计算转码分段数。修改后用 8.1 复测通过；本机未能运行 6.1 复现，以 CI 结果为准。
- 开发中发现并修正：ffmpeg 的 HLS 封装默认让每个任务的分段时间戳从 0 开始，拖动后重启的分段会错位，加上 `movflags=+frag_discont` 后保留原时间轴；转封装的输出端定位会平移时间戳，用 `-output_ts_offset` 补回；AAC 编码延迟让音频起点为负，写入分段后变成极大的值，改用 `make_non_negative`。
- `node tests/browser/smoke.cjs`（带 ffmpeg，Playwright 自带的 Chromium）通过：VP9 / FLAC 的 MKV 自动选择直接播放；切换为转封装后 hls.js 以 MediaSource 播放，拖动到 8.5 秒后继续播放；离开时结束服务端会话；H.264 视频的字幕菜单列出内封 ASS 与烧录选项，浏览器无法解码 H.264 时不提供转封装。无 JS 错误。之后又连续运行了 14 次，全部通过；开发过程中出现过一次断言失败，没有保存完整日志，之后再未复现，原因未查明。
- 一次性检查（未加入测试集）：用本机安装的 Chrome（支持 H.264 和 HEVC 硬解）打开同一批视频：HEVC 10bit 与 H.264 自动直接播放；手动转码为 480p 后拖动到 9 秒继续播放（854×480）；内封 ASS 作为字幕轨显示；烧录字幕后拖动到 5 秒，字幕在画面顶部、与时间轴同步。
- 无 ffmpeg 时的 `integration.py`、`smoke.cjs`，以及 `integration.py --real-aria` 通过，P2 前的播放行为不变。
- 未验证：Docker 镜像构建（本机没有 Docker，`WITH_FFMPEG` 构建参数只检查了 shell 语法和分支）、显卡直通与硬件编码器（NVENC / QSV / VAAPI / VideoToolbox / AMF 只验证了参数生成）、Safari / iOS 的原生 HLS、真实字幕组发布的大文件（数 GB 的 MKV 提取内封字幕与关键帧需要读完整个文件）、同时转码上限与闲置回收（依赖时间，未写自动测试）。

## 2026-11-26 媒体库第二阶段（海报墙与播放）

- `dotnet build -c Release` 通过，无警告、无错误；80 项核心测试通过（新增 9 项）。覆盖：字幕组语言标记（简繁、双语、粘连写法如 `jpsc`、中文标记）；字幕查找（同名、`Subs` 文件夹、不同命名时按作品与集数匹配、排除其他集）；SRT / ASS 转 WebVTT（标签过滤与转义、`\N` 换行、顶部对齐、跳过注释、绘图与倒序时间行）；UTF-8 BOM、UTF-16 与 GBK 解码；继续观看的选集规则；集数修正跨扫描保留、文件消失后清除；进度的 90% 看完规则、重看不改变已看、标为未看；签名链接被篡改、换密钥、改密码和文件删除后失效；推送播放链接；下载目录缺失或为空时保留索引。
- `python tests/integration.py` 通过：视频 Range 请求（206、`Content-Range`、MKV 类型）、播放页数据、字幕转 WebVTT、不存在的视频与字幕返回 404、进度校验与继续观看、整部 / 单集标记、签名链接免登录访问（Range 与 HEAD）、篡改链接与未登录访问 API 被拒绝、M3U 链接、Bangumi 搜索、未配置 AI 时拒绝识别、模拟 AI 识别（含代码块包裹的 JSON 和多余条目）、识别结果跨扫描保留、手动修改与非法值、恢复自动识别、访问地址校验。
- `python tests/integration.py --real-aria` 通过：真实 aria2 下载完成一个本地 `.mkv` 后，Bark 推送带有正确的播放页链接。
- `node tests/browser/smoke.cjs` 通过：用 Playwright 自带 ffmpeg 录制几秒的 WebM 作为测试视频；海报墙搜索与筛选、作品页分集标题与资料来源、修正匹配的 Bangumi 搜索、点击后自动播放、默认简体字幕与 ASS 换行 / 顶部对齐、字幕选择记忆、快捷键与暂停时保存进度、下载 M3U 并免登录读取视频、播完显示下一集并自动标记已看、继续观看、单集标记、无法解码的文件提示外部播放器、未知视频提示；390px 手机媒体库页无横向溢出；无 JS 错误。另截图检查了深色模式下的海报墙、作品页、播放页，以及手机布局。
- 一次性检查（未加入测试集）：32 秒视频从 0:15 继续播放、「从头播放」、离开页面时保存进度。
- `notifications.py`、`real_downloads.py`、`local_tuning.py`、`mikan_fallback.py` 通过。
- 未在真实浏览器中验证 HEVC / H.265、10bit、FLAC 等格式的实际表现（测试环境的 Chromium 不含专有解码器），也未在真机上验证 PotPlayer、IINA、Infuse、VLC、MX Player、nPlayer 的唤起；外部播放器通过签名链接读取视频的部分已测试。

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
