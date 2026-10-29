# Hi3Helper.Plugin.NTE

为 Collapse Launcher 提供《异环 / Neverness to Everness》中国大陆、国际服和台港澳三个独立地区，包含安装更新、完整性检查、游戏启动和账号登录。当前版本为 0.12.1，目标平台为 Windows x64，使用 Plugin Core API 0.1.6。

本插件使用自行实现的登录流程，未经游戏官方认可或授权。可能触发风控、登录限制、账号封禁或永久封号，无法保证账号安全和持续兼容。使用前请阅读[登录说明与隐私](docs/login-privacy.md)。

因插件的特殊性，不得在任何公开社交媒体面向不特定人群宣传或推荐本插件，例如 B站、抖音、公开论坛，国际平台如 YouTube、TikTok、Reddit、X 等。上述仅为举例，并非完整清单。否则，作者将直接停止维护并删除仓库（删库跑路）。

## 使用

从本仓库 Actions 的成功构建下载 `NTE-ReleasePublish-O1`，将其中 `NTE_*.zip` 导入 Collapse。插件包包含自包含的 WPF 登录宿主，无须另行安装 .NET 桌面运行时。官方游戏和启动组件仍须位于所选安装目录。

首次使用独立登录时会直接展示说明，只有点击“我知道了”并成功保存后，下次才不再默认展示。之后可通过面板侧面的角色头像重新查看。

台港澳使用独立登录宿主，支持 Google、Facebook 及手机号验证码／密码登录，区号为台湾、香港和澳门；其账号、设备和设置与国际服隔离。已安装的官方组件仍是经过校验的 SDK 和静态配置来源。0.12.1 修复 LocalLow 安装目录的浏览器回调，并恢复台服活动、新闻、社交图标和繁体标题资源。

国际服和台港澳可以共享同一安装根目录及 `Client`。在游戏设置的“服务器与共享安装”中选择目标地区，先检查预览再执行切换；首次启用会校验现有安装、准备差异文件并保留恢复状态。详见[地区与共享安装](docs/regions.md)。

不希望使用独立登录时，可在 Collapse → 游戏设置 → 启动设置启用“使用官方启动器后台静默启动”。官方启动器必须位于同一游戏安装目录内：国服 `NTELauncher/NTEGame.exe`，国际版 `NTEGlobal/NTEGlobalGame.exe`，台港澳 `NTETW/NTETWGame.exe`。说明页也提供此选项；确认后保存该区服设置并关闭当前游戏，下次启动生效。官方登录、验证或更新所需窗口仍可能出现。

## 构建

使用 Windows x64、`global.json` 指定的 .NET SDK 和 Visual Studio C++ 桌面工具链。首次获取源码后初始化固定版本的子模块：

```powershell
git submodule update --init --recursive
./scripts/Build.ps1 -Configuration Release
./scripts/Publish.ps1 -Profile ReleasePublish-O1 -Package
```

构建脚本执行锁定还原、编译和离线回归，报告位于 `artifacts/test-results/`；三个需要访问官方在线服务的显式测试默认不运行。发布脚本验证原生 ABI、自包含运行时和完整性检查接口，生成可导入包到 `artifacts/packages/`。离线回归、界面预览和公开 CDN 读取不能代替真实账号认证或游戏进入验收。

GitHub Actions 在 `main` 的推送和拉取请求时自动运行测试，并生成普通与 NoReflection 两种 NativeAOT 包。也可手动运行构建。`Release NTE Plugin` 工作流允许手动指定版本制作 GitHub Release。

## 源码结构

- `src/Hi3Helper.Plugin.NTE`：Collapse 插件、安装更新、媒体、游戏设置及启动交接。
- `src/Hi3Helper.Plugin.NTE.LoginHost`：独立 WPF 登录窗口、账号保护与游戏会话生命周期。
- `src/Hi3Helper.Plugin.NTE.SdkShared`：宿主、插件与测试共用的协议和设置代码。
- `tests`：使用虚构输入的离线回归与界面背景夹具。
- `tools/NteLoginUiVerification`、`tools/NteLoginHostHarness`：离屏 WPF 界面验证，不属于交付包。

[架构说明](docs/architecture.md) · [地区与共享安装](docs/regions.md) · [构建与验证](docs/development.md) · [登录说明与隐私](docs/login-privacy.md)

## 许可与标识

源代码许可证见 [LICENSE](LICENSE)。保留 Collapse 样式的原作者声明与 MIT 许可证；Google、Facebook 标识归各自权利人所有，来源保存在对应资产目录的 `UPSTREAM.json`。本插件不隶属于游戏运营方，也不随包分发官方启动器、更新器或账号数据。`Indexer.exe` 为插件包元数据构建工具，仅用于制作发布包。
