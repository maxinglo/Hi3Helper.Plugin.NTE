<div align="center">

# Hi3Helper.Plugin.NTE

为 [Collapse Launcher](https://collapselauncher.com/) 提供《异环 / Neverness to Everness》支持的第三方插件。

**中国大陆 · 国际服 · 台港澳**

[下载插件](https://github.com/maxinglo/Hi3Helper.Plugin.NTE/releases) · [开发构建](https://github.com/maxinglo/Hi3Helper.Plugin.NTE/actions/workflows/build.yml) · [反馈问题](https://github.com/maxinglo/Hi3Helper.Plugin.NTE/issues)

</div>

> [!IMPORTANT]
> 本插件由社区维护，并非 Collapse 或游戏官方插件。使用中遇到的问题请在本仓库反馈。

> [!CAUTION]
> 插件使用自行实现的登录流程，未经游戏官方认可或授权，可能触发风控、登录限制或账号封禁，无法保证账号安全及持续兼容。使用前请阅读[登录说明与隐私](docs/login-privacy.md)，也可选择使用官方启动器登录。

## 功能

- 下载、安装、更新游戏，识别已有安装目录。
- 检查文件完整性并修复损坏、缺失的资源。
- 展示官方新闻、公告、活动、轮播和社交媒体入口。
- 显示动态背景、版本标题图和游戏标识，支持调整装饰图片的位置与大小。
- 提供独立登录窗口，支持保存账号、账号备注及恢复上次登录。
- 使用当前 Windows 用户加密保护账号，可选 Windows Hello 保护。
- 选择游戏默认图形接口、DirectX 11 或 DirectX 12，也可使用官方启动器启动。
- 国际服与台港澳支持共用游戏目录、下载差异文件及切换服务器。

| 地区     | 登录方式                           |
|----------|------------------------------------|
| 中国大陆 | 手机验证码                         |
| 国际服   | 邮箱、Google、Facebook             |
| 台港澳   | 手机验证码／密码、Google、Facebook |

各地区的账号和设置分别保存。共用安装目录不会转换账号，也不会合并角色或服务端数据。台港澳手机号支持台湾、香港及澳门区号。

## 安装插件

需要 Windows 10／11 x64，以及支持 **Plugin Core API 0.1.6** 的 Collapse Launcher。

1. 从本仓库下载插件压缩包 `NTE_*.zip`。开发构建可在 [Actions](https://github.com/maxinglo/Hi3Helper.Plugin.NTE/actions/workflows/build.yml) 的成功运行中下载
   `NTE-ReleasePublish-O1` 产物，解压外层后取得插件 ZIP。
2. 打开 Collapse 的 **设置 → 插件管理**，选择添加 ZIP 或 `manifest.json`，导入插件 ZIP。
3. 重启 Collapse，在游戏列表中选择 **异环** 及对应地区。
4. 安装游戏，或选择已有的游戏安装目录。

插件包已包含登录窗口所需的 .NET 桌面运行时，无须单独安装。更新插件时同样导入新版本 ZIP；账号和游戏文件不保存在插件目录中。

## 从源码构建

需要 `global.json` 指定的 .NET SDK、PowerShell 7、Visual Studio C++ 桌面工具链及 Windows SDK。

```powershell
git submodule update --init --recursive
./scripts/Build.ps1 -Configuration Release
./scripts/Publish.ps1 -Profile ReleasePublish-O1 -Package
```

可导入的 ZIP 位于 `artifacts/packages/`。构建脚本会运行回归测试，发布脚本会检查原生接口、自包含登录宿主和包内容。更多说明见[构建与验证](docs/development.md)。

[架构说明](docs/architecture.md) · [地区与共享安装](docs/regions.md) · [登录说明与隐私](docs/login-privacy.md)

## 使用限制与许可

因插件的特殊性，不得在任何公开社交媒体面向不特定人群宣传或推荐本插件，例如 B站、抖音、公开论坛，国际平台如
YouTube、TikTok、Reddit、X 等。上述仅为举例，并非完整清单。否则，作者将直接停止维护并删除仓库（删库跑路）。

本项目与 Perfect World、Hotta Studio、艾玩天地及其关联实体无隶属关系。源代码许可证见 [LICENSE](LICENSE)。所用 Collapse
样式保留原作者声明及 MIT 许可证；Google、Facebook 标识归各自权利人所有。
