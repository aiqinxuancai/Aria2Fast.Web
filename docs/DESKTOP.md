# 桌面运行与发布

Windows 和 macOS 在 HTTP 服务监听成功后打开默认浏览器。使用实际绑定的端口，通配绑定转换为 localhost。端口占用时依次增加 1，最多尝试 100 个端口；其他错误直接报告。显式配置 `Kestrel:Endpoints` 时不自动修改端口。

设置中心的「桌面与启动」提供：

- 开机自动启动：当前用户登录后隐藏控制台运行。Windows 使用当前用户 Run 注册项与隐藏窗口启动器；macOS 使用用户 LaunchAgent，下次登录生效。
- 创建桌面快捷方式：Windows 创建 `.lnk`，macOS 创建无终端窗口的 `.app` 启动器。启动程序后自动打开浏览器。

初始密码仍保存在数据目录的 `initial-password.txt`。关闭浏览器不会结束服务。移动程序或数据目录后应重新创建快捷方式，并关闭再打开自启。macOS 系统可能要求允许后台项目；Windows 需要启用 Windows Script Host。

Docker 不打开浏览器，也不显示桌面选项，相关 API 同时拒绝操作。其他无桌面部署可设置 `ARIA2FAST_DESKTOP=false`。目前仅 Windows、macOS 支持桌面集成。

## 发布方式

在 Windows .NET 10 下实际尝试 Native AOT 编译，出现 JSON 反射及 Aliyun OSS SDK AOT/裁剪警告。产物启动时在 `StateStore.Clone` 抛出 `Reflection-based serialization has been disabled for this application`，当前代码不能直接可靠地 AOT 发布。

桌面 Release 改用 `Properties/PublishProfiles/Desktop.pubxml`：自包含单文件、不裁剪、不开启 AOT，运行无需另装 .NET。原生运行时依赖由单文件自动解压。`wwwroot`、配置文件、aria2 及其动态库和许可证仍随压缩包提供，因此“单文件”指 Web 主程序，不是整个下载器只有一个文件。

```sh
dotnet publish src/Aria2Fast.Web -c Release -r win-x64 -p:PublishProfile=Desktop -o artifacts/publish
```

macOS 使用 `osx-x64` 或 `osx-arm64`。Docker 保持常规框架依赖发布。

Release 对各平台的发布产物运行接口、下载和端口冲突测试。`tests/desktop_startup.py` 连续占用两个端口，验证最终地址、静态文件及关闭桌面功能时的 API 拒绝行为。桌面 UI 测试模拟系统操作，不修改测试机器的自启和桌面。
