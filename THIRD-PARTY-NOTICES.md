# 来源与第三方组件

本项目衍生自 https://github.com/aiqinxuancai/Aria2Fast，保留原项目 GPL-3.0 许可证（LICENSE.txt）。

- Shared/MatchUtils.cs、Shared/AiProtocolType.cs 及 logo.png 来自桌面项目。
- MikanService 的页面选择器及订阅语义改写自桌面 MikanManager、SubscriptionManager。
- aria2：https://github.com/aria2/aria2，GPL-2.0-or-later。Windows 发行包使用 1.37.0 官方预编译文件并保留随附文档；对应源码：https://github.com/aria2/aria2/releases/tag/release-1.37.0 。
- Docker 通过发行版软件包安装 aria2；包版权位于容器 /usr/share/doc/aria2/copyright，精确版本可运行 aria2c --version 查询。
- macOS 打包 Homebrew aria2 及其非系统动态库。aria2/licenses 内包含各 formula 版本、源码 URL 及已安装的许可证；bundle-manifest.json 记录依赖来源。
- HtmlAgilityPack：MIT，https://github.com/zzzprojects/html-agility-pack。
- Aliyun OSS .NET SDK：Apache-2.0，https://github.com/aliyun/aliyun-oss-csharp-sdk。
- ASP.NET Core / .NET：MIT，https://github.com/dotnet/aspnetcore。
- hls.js 1.7.4（light 构建的 ES 模块，wwwroot/vendor/hls.light.esm.js，未修改，SHA-256 3d82a88cf9ae0d9aaab3cbe9ff6ecbb64bb89c54a399c34ae9bf8b559baeb20f）：Apache-2.0，https://github.com/video-dev/hls.js ，许可证见 wwwroot/vendor/hls.js-LICENSE.txt。
- FFmpeg 不随本项目分发，由用户自行安装（LGPL / GPL，取决于构建）。Docker 镜像只有在构建参数 WITH_FFMPEG=true 时才安装发行版的 ffmpeg 软件包，其版权信息位于容器 /usr/share/doc/ffmpeg/copyright。

Mikan、TMDB、Tavily、PushDeer、Bark、AI 服务均为可选外部服务，内容与商标归各自权利人。TMDB 集成使用 TMDB API，本应用未获 TMDB 认可或认证。
