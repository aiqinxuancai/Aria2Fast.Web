# 部署与配置

本文是 [README](../README.md) 快速开始的补充，介绍 Docker、桌面发行包的完整配置，以及下载调优和网络相关设置。

## Docker

默认 `compose.yaml` 只拉取 GHCR 镜像，不需要源码或 Dockerfile，可直接用于 Unraid Compose Manager 等管理器。

1. 复制 `.env.example` 为 `.env`，填写 `ARIA2FAST_PASSWORD`（至少 12 个字符；留空则自动生成随机密码）。
2. 执行 `docker compose pull`，然后 `docker compose up -d`。

镜像内置 aria2，以非 root 用户运行，对外公开 Web `8080`、BT TCP `16888` 和 DHT UDP `16888` 端口；Aria2 RPC 只在容器内部监听。

**指定镜像版本**：将 `.env` 中的 `ARIA2FAST_IMAGE` 设置为 `ghcr.io/<用户名>/<仓库名>:v1.0.0`（仓库名必须小写），再执行 `docker compose pull` 和 `docker compose up -d --no-build`。

**带 ffmpeg 的镜像**：媒体库要转码播放浏览器不支持的视频时，把 `ARIA2FAST_IMAGE` 设置为 `ghcr.io/<用户名>/<仓库名>:latest-ffmpeg`（或 `v1.0.0-ffmpeg`）。它另外包含 ffmpeg 和 VAAPI 驱动；用显卡转码需要把 `/dev/dri` 或 NVIDIA GPU 开放给容器，仓库提供了 `compose.vaapi.yaml` 和 `compose.nvidia.yaml`，用法见 [媒体库 · Docker 与显卡](MEDIA-LIBRARY.md#docker-与显卡)。

**从源码构建**：在含 Dockerfile 的完整项目根目录执行：

    docker compose -f compose.yaml -f compose.build.yaml up -d --build

构建带 ffmpeg 的镜像时，在命令前加上 `ARIA2FAST_WITH_FFMPEG=true`。本地构建配置单独放在 `compose.build.yaml`，使用镜像部署时不要启用这个覆盖文件。旧版配置请移除 `build: .`；如果还有 `pull_policy: build` 也应移除，否则管理器可能跳过拉取，尝试本地构建。

### 数据与下载目录

| 卷 | 容器路径 | 内容 |
| --- | --- | --- |
| `app-data` | `/data` | 配置、订阅历史、Aria2 会话、Cookie 密钥、初始密码 |
| `downloads` | `/downloads` | 下载的文件 |

- 查看随机初始密码：`docker compose exec aria2fast cat /data/initial-password.txt`
- 想把文件直接存到宿主机目录，可把命名卷改为目录映射，例如 `/mnt/media/downloads:/downloads`。容器以 `app` 用户（UID 1654）运行，需确保该目录可读写，例如 `sudo chown -R 1654:1654 /mnt/media/downloads`。
- 更新镜像时保留两个卷。不要用 `docker compose down -v`，它会删除数据卷。

### 环境变量

| 环境变量 | 用途 |
| --- | --- |
| `ARIA2FAST_PASSWORD` | 至少 12 个字符的管理员密码，每次启动时应用；设置后密码由环境变量管理 |
| `ARIA2FAST_DATA_DIR` | 配置、订阅历史、Aria2 会话和 Cookie 密钥目录 |
| `ARIA2FAST_DOWNLOAD_DIR` | 首次运行时的默认本地下载目录 |
| `ARIA2FAST_LOCAL_ENABLED` | `true` / `false`，启动时覆盖本地引擎开关 |
| `ARIA2FAST_DESKTOP` | `false` 关闭桌面集成（自动打开浏览器、自启等） |
| `ASPNETCORE_URLS` | Web 监听地址，默认 `http://0.0.0.0:8080` |

## Windows / macOS 发行包

从 [Releases](https://github.com/aiqinxuancai/Aria2Fast.Web/releases) 下载对应平台的压缩包，解压到可写目录：

- Windows：`Aria2Fast.Web-win-x64.zip`，运行 `Start.cmd`
- macOS Intel：`Aria2Fast.Web-osx-x64.tar.gz`
- macOS Apple Silicon：`Aria2Fast.Web-osx-arm64.tar.gz`

macOS 运行 `Start.command`，或在终端执行 `./Aria2Fast.Web`。

发行包自带 .NET 运行时和 aria2，macOS 包也包含所需动态库，无需安装 Homebrew。目前 macOS 版本没有 Apple Developer 签名或公证，首次启动可能需要在「系统设置 → 隐私与安全性」中允许。关闭终端窗口会停止服务。

默认数据目录是程序目录下的 `data`，可用 `ARIA2FAST_DATA_DIR` 指定固定位置。初始密码保存在数据目录的 `initial-password.txt`，终端会显示它的实际路径。

开机自启、桌面快捷方式和端口占用处理见 [桌面集成](DESKTOP.md)。

## 公网访问与安全

- 公网访问请使用 HTTPS 反向代理，并尽量限制访问来源。
- 不要对外公开 Aria2 RPC 端口。
- 配置文件包含服务密钥，请保护好数据目录或数据卷；登录后的设置页面会向浏览器返回可编辑的密钥。
- 账号为单管理员，不提供多用户隔离。

## 下载调优

「设置中心 → 下载设置」提供 BT TCP / DHT UDP 端口、HTTP 连接与分片、BT 连接上限、总上传/下载限速、DHT 和 PEX 等选项。

- 总上传/下载限速保存后立即生效，已有任务也会生效；其他调优参数保存后，需要在节点管理中重启本地服务。
- 配置随应用数据持久化，不要手动编辑启动时生成的 `aria2.conf`。
- 默认同时下载 3 个任务、HTTP 8 连接 / 8 分片、BT 最多 128 个连接；已保存的全局参数优先。
- HTTP 分片对 BT 无效；没有人做种的资源，调整参数也无法保证提速。

### 公共 Tracker

公共 Tracker 自动更新默认关闭。只下载公共 BT 资源时，可以开启它并点击「保存并更新 Tracker」，默认每天从 [ngosang/trackerslist](https://github.com/ngosang/trackerslist) 精选列表更新。

- 最多支持 5 个列表来源，接受 HTTP / HTTPS / UDP Tracker，去重后最多保留 100 个。
- 下载列表时使用设置中的网络代理；失败时保留上次有效缓存，一小时后重试。
- 手动 Tracker 与自动列表合并，种子自带的 Tracker 不会被删除。
- 更新只影响后续新任务，已有任务不会自动修改；关闭此功能后请重启本地服务。
- **PT 私有种子不要开启公共 Tracker。**

### 连接诊断

「连接诊断」显示 RPC 状态、实际参数、本机监听端口、Tracker 更新状态，以及活跃 BT 任务的连接数、做种连接数和下载速度。它不进行公网探测，本机正在监听并不代表外部可达。

Docker 默认映射 16888 TCP / UDP。修改设置中的端口后，请同步修改 `compose.yaml` 的端口映射，并按网络环境配置路由器端口转发和防火墙。

## 资源搜索源

「探索番剧」页顶部可以在「番剧日历」和「资源搜索」之间切换。番剧详情里的「在其他下载源中搜索」会带上作品名直接搜索，用来补充 Mikan 上没有的资源。

在「设置中心 → 资源搜索源」中可以：

- 启用或停用 ACG.RIP、Nyaa、动漫花园、末日动漫（前三个默认启用）。
- 为每个站点填写镜像或反向代理地址，支持带路径前缀，例如 `https://mirror.example/dmhy`；留空使用官方地址。
- 添加 Torznab 索引器（Prowlarr / Jackett），填写 Torznab 地址、API Key 和分类（5070 为动漫），可先点「测试连接」确认。

所有请求都通过「网络代理地址」发出。本应用**不内置**绕过 Cloudflare 等人机验证的功能；某个站点触发验证时，搜索结果会提示原因，请改用镜像，或在 Prowlarr / Jackett（可搭配 FlareSolverr）中接入该站点。

搜索结果旁的「＋ 订阅」会把这次搜索的 RSS 地址转为普通订阅，之后可以和其他订阅一样设置过滤规则、目录和 AI 分目录。Torznab 订阅地址中不保存 API Key，由服务器在请求时自动补上；备份和 OSS 同步中下载记录的 API Key 也会被隐藏。删除索引器后，用它创建的订阅将无法再获取数据。

### 服务端预取种子

「设置中心 → 下载设置 → 订阅与网络」中的「由服务端经代理预取种子文件」默认开启。开启后，`.torrent` 链接和 Torznab 下载链接会先由 Web 服务通过网络代理下载，并校验种子格式，再提交给下载节点：

- 站点返回错误页面或损坏的文件时直接报错，不会创建无效任务。
- 下载链接跳转到磁力链（Prowlarr / Jackett 常见）时，自动改用磁力链。
- Web 服务连不上站点时，改由下载节点直接下载，并在活动记录中提示。

Mikan 种子始终由服务端预取，不受此开关影响。

## Mikan 种子备用站点

「设置中心 → 下载设置 → 订阅与网络」中的「Mikan 种子备用站点」默认为 `https://mikanani.me`，留空则禁用。

原链接获取失败、超时或返回的不是种子时，会保留路径和查询参数，改用备用站点重试。种子由 Web 服务通过网络代理获取，再提交给所选下载节点。新建任务、订阅任务和旧失败任务的「重试」都会使用这个设置，保存后无需重启。两个站点都失败时会分别显示原因。此设置不会切换番剧页面或 RSS 的源站。
