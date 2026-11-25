# 开发与发布

Aria2Fast Web 由桌面版 [Aria2Fast](https://github.com/aiqinxuancai/Aria2Fast) 改写为 ASP.NET Core 10 Web 应用，可独立构建，不依赖 WPF 或原项目目录。

## 源码运行

需要 .NET 10 SDK。在项目根目录执行：

    dotnet run --project src/Aria2Fast.Web

然后访问 http://localhost:8080 。

源码运行需要自行安装 aria2，或在设置中指定它的执行文件。Windows 可以执行 `scripts/install-aria2.ps1`，默认安装到 Debug 输出目录下的 `aria2` 子目录。Docker 镜像和 GitHub Release 发行包会自动包含 aria2。

## 测试

    dotnet build -c Release
    dotnet run --project tests/Aria2Fast.Web.Tests -c Release --no-build
    python tests/integration.py
    cd tests/browser
    npm install
    npx playwright install chromium
    node smoke.cjs

- 核心测试不需要额外的测试框架；接口测试用本地 HTTP fixtures 模拟 RPC、Mikan 和 AI 服务。
- 发行 CI 还会用 `tests/integration.py --real-aria` 和 `tests/real_downloads.py`，验证捆绑的 aria2 能真实下载，以及会话恢复、Torrent 和 Metalink。
- 常规 CI 运行 `tests/browser/real.cjs`，通过模拟手机浏览器下载并校验取回的文件。Docker CI 验证容器启动、认证和本地 RPC。
- 测试截图输出到 `artifacts/screenshots`。实测命令、结果和未验证范围见 [TESTING.md](TESTING.md)，功能迁移差异见 [FEATURES.md](FEATURES.md)。

## README 截图

README 中的截图由真实前端配合演示数据生成，作品名、海报和评析内容均为虚构：

    dotnet build -c Release
    node tests/browser/readme-screenshots.cjs

截图输出到 `docs/images`。界面有明显变化时重新运行即可。

## GitHub Actions 发布

把本目录作为独立 GitHub 仓库的根目录。推送任意 tag 会触发 `.github/workflows/release.yml`：先运行检查，再发布：

- GHCR 镜像：linux/amd64、linux/arm64
- 自包含发行包：Windows x64、macOS Intel x64、macOS Apple Silicon arm64
- `SHA256SUMS.txt` 和当前源码归档

带连字符的 tag（如 `v1.0.0-beta`）标记为预发行版；不带连字符的 tag 会同时更新 `latest`。

流程使用仓库自带的 `GITHUB_TOKEN`，声明了 `packages:write` 和 `contents:write`；组织策略仍需允许发布 Packages 和 Release。首次公开镜像时，可能需要在 GHCR 包设置中调整可见性。

桌面发行包的发布方式（单文件、为何不用 Native AOT）见 [桌面集成](DESKTOP.md)。

## 代码规范

源代码按项目规范使用 CRLF，C# 文件为 UTF-8 BOM；Shell、Dockerfile 与 workflow 使用适合 Linux 执行的 LF。

许可证见 [LICENSE.txt](../LICENSE.txt)，第三方组件归属见 [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md)。
