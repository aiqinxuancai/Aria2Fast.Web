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

Mikan、TMDB、Tavily、PushDeer、AI 服务均为可选外部服务，内容与商标归各自权利人。TMDB 集成使用 TMDB API，本应用未获 TMDB 认可或认证。
