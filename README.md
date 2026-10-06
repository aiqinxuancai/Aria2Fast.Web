# Aria2Fast Web

基于桌面 Aria2Fast 改写的 ASP.NET Core 10 Web 应用，独立构建，不依赖 WPF 或原项目目录。

## 启动

在项目根目录执行：

    dotnet run --project src/Aria2Fast.Web

访问 http://localhost:8080 。手机使用同一局域网内服务器的 IP 和 8080 端口，Windows 防火墙需允许访问。

首次启动自动生成随机密码，终端显示 initial-password.txt 的实际路径。默认数据目录位于程序输出目录下的 data；可通过 ARIA2FAST_DATA_DIR 指定固定目录。登录后在设置中心修改密码。也可用 ARIA2FAST_PASSWORD 环境变量设置至少 12 字符的密码，此时密码由环境变量管理。

源码开发运行需要自行安装 aria2 或指定其执行文件；Windows 可执行 scripts/install-aria2.ps1，默认安装到 Debug 输出目录的 aria2 子目录。Docker 与 GitHub Release 流程自动包含 aria2。

## Docker

复制 .env.example 为 .env，填写 ARIA2FAST_PASSWORD，然后执行 docker compose pull 和 docker compose up -d。默认 compose.yaml 仅拉取 GHCR 镜像，可直接用于 Unraid Compose Manager，无需源码或 Dockerfile。镜像内安装真实 aria2 执行文件，以非 root 用户运行，公开 Web 8080、BT TCP 6888 和 DHT UDP 6888 端口；RPC 保持容器内部监听。

使用 GHCR：将 .env 中 ARIA2FAST_IMAGE 设置为 ghcr.io/你的组织或用户名/仓库名:v1.0.0（仓库名须小写），执行 docker compose pull，然后 docker compose up -d --no-build。

从源码本地构建：在含 Dockerfile 的完整项目根目录执行 docker compose -f compose.yaml -f compose.build.yaml up -d --build。本地构建配置单独放在 compose.build.yaml，镜像部署时不要启用此覆盖文件。旧版配置请移除 build: .；若另有 pull_policy: build 也应移除，否则管理器可能跳过拉取并尝试构建。

数据卷 app-data 映射 /data，downloads 映射 /downloads。查看随机初始密码：docker compose exec aria2fast cat /data/initial-password.txt。可将命名卷改为宿主机目录，确保容器 app 用户可读写。更新镜像时保留两个卷。不要使用 down -v 删除需要保留的数据。

| 环境变量 | 用途 |
| --- | --- |
| ARIA2FAST_DATA_DIR | 配置、订阅历史、Aria2 会话和 Cookie 密钥目录 |
| ARIA2FAST_DOWNLOAD_DIR | 首次运行默认本地下载目录 |
| ARIA2FAST_PASSWORD | 至少 12 字符的管理员密码，重启时应用 |
| ARIA2FAST_LOCAL_ENABLED | true / false，启动时覆盖本地引擎开关 |
| ASPNETCORE_URLS | Web 监听地址，默认 http://0.0.0.0:8080 |

公网访问请使用 HTTPS 反向代理并限制访问范围，不要公开 Aria2 RPC 端口。配置文件包含服务密钥，应保护数据卷；管理员设置接口会向已登录浏览器返回可编辑的密钥。

## 功能

- HTTP / FTP / Magnet 批量添加，Torrent / Metalink 上传；进度、速度、暂停/继续、批量移除、队列置顶、限速、Tracker、种子选文件及 Peers。
- 本地 Aria2 生命周期管理、会话保存、远程节点管理及切换；本地全局参数在重启后恢复。
- RSS / Atom 自动轮询、关键词 OR / 正则 / 排除过滤、匹配预览；新订阅默认补齐已有资源，可选仅追更；支持重新下载全部匹配资源、下载历史去重、季度目录、AI 按作品分目录。
- Mikan 季度与星期浏览、搜索、字幕组订阅、单条资源下载；可选 TMDB 评分/投票/热度/简介、AI 翻译与评析、Tavily 参考搜索。
- OpenAI Chat Completions、Responses、Claude Messages、Gemini 四类 AI 协议；多组配置管理。
- 已完成本地文件浏览器取回、AI 重命名预览与确认；PushDeer 下载完成推送、活动记录。
- 订阅 JSON 导出/合并导入、桌面端订阅数组兼容、OSS 手动上传、合并与可选定时同步。
- 手机底部导航、响应式布局、浅色/深色/跟随系统并实时响应系统变化。

具体迁移差异和测试范围见 docs/FEATURES.md。图片、评分、下载记录均来自实际接口；无数据时显示空状态。

## 本地下载调优

设置中心 → 下载设置提供 BT TCP / DHT UDP 端口、HTTP 连接与分片、BT 连接上限、总上传/下载限速、DHT 和 PEX。保存后在节点管理中重启本地服务，配置会随应用数据持久化；不要手动编辑启动时生成的 aria2.conf。默认同时下载 3 个任务、HTTP 8 连接 / 8 分片、BT 最多 128 个连接；已经保存的全局参数优先。

公共 Tracker 自动更新默认关闭。仅下载公共 BT 时，可开启并点击“保存并更新 Tracker”，默认每天从 ngosang/trackerslist 的精选列表更新。支持最多 5 个列表来源，接受 HTTP / HTTPS / UDP Tracker，去重并最多保留 100 个。下载列表使用设置中的网络代理；失败保留上次有效缓存，并在一小时后重试。手动 Tracker 与自动列表合并，原种子 Tracker 不会被删除。更新用于后续新任务，已有任务不自动修改；关闭此功能后请重启本地服务。PT 私有种子不要开启公共 Tracker 功能。

“连接诊断”显示 RPC、实际参数、本机监听端口、Tracker 更新状态，以及活跃 BT 任务连接数、做种连接数和下载速度。它不进行公网探测，不能据本机监听判断外部可达。Docker 默认映射 6888 TCP / UDP；修改设置中的端口时，请同步修改 compose.yaml 的映射，并按网络环境配置路由器与防火墙。HTTP 增加分片对 BT 无效，无人做种的资源无法靠参数保证提速。

## Windows / macOS 发行包

解压到可写目录，Windows 运行 Start.cmd，macOS 运行 Start.command 或终端 ./Aria2Fast.Web。发行包自包含 .NET 运行时与 aria2；macOS 包含非系统动态库，无需用户安装 Homebrew。当前未配置 Apple Developer 签名或公证，macOS 可能要求在系统隐私与安全设置允许启动。停止终端进程会停止服务器。

## GitHub Actions

将本目录作为独立 GitHub 仓库根目录。推送任意 tag 触发 .github/workflows/release.yml：先检查，再发布 GHCR 的 linux/amd64、linux/arm64 镜像以及 Windows x64、macOS Intel x64、macOS Apple Silicon arm64 自包含发行包。附带 SHA256SUMS.txt 和当前源码归档。带连字符的 tag 标记为预发行版；不带连字符的 tag 同时更新 latest。

使用仓库 GITHUB_TOKEN，流程声明 packages:write 和 contents:write；组织策略仍需允许发布 Packages/Release。首次公开镜像可能需要在 GHCR 包设置中调整可见性。本地工作没有推送仓库或 tag，不会自动触发远程发布。

## 验证

    dotnet build -c Release
    dotnet run --project tests/Aria2Fast.Web.Tests -c Release --no-build
    python tests/integration.py
    cd tests/browser
    npm install
    npx playwright install chromium
    node smoke.cjs

核心测试无需额外测试框架；接口测试用本地 HTTP fixtures 替代 RPC/Mikan/AI。发行 CI 另用 tests/integration.py --real-aria 和 tests/real_downloads.py 验证真实捆绑 aria2 下载、会话恢复、Torrent 和 Metalink。常规 CI 还运行 tests/browser/real.cjs，通过手机模拟浏览器下载并校验取回文件。Docker CI 验证容器启动、认证和本地 RPC。截图位于 artifacts/screenshots；真实测试命令、实测结果和未验证范围见 docs/TESTING.md。

源代码按项目规范使用 CRLF，C# 文件为 UTF-8 BOM；Shell、Dockerfile 与 workflow 使用适合 Linux 执行的 LF。许可证见 LICENSE.txt，第三方归属见 THIRD-PARTY-NOTICES.md。
