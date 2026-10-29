# 构建与验证

依赖版本由 `global.json`、包锁文件和固定子模块提交确定。`Indexer.exe` 用于写入 Collapse 所需的原生插件元数据；`New-NteImportPackage.ps1` 保留清单路径分隔符，避免导入器匹配失败。

```powershell
./scripts/Build.ps1 -Configuration Release
./scripts/Build.ps1 -Configuration ReleaseNoReflection
./scripts/Publish.ps1 -Profile ReleasePublish-O1 -Package
./scripts/Test-NteImportSimulation.ps1 -ArchivePath artifacts/packages/NTE_<version>_ReleasePublish-O1.zip
./scripts/Test-NteLoginUx.ps1 -HostDirectory artifacts/publish/ReleasePublish-O1/LoginHost -Languages zh-CN,en
```

离线回归不等于真实游戏接受登录；界面夹具不证明真实 Windows Hello 手势、混合 DPI、背景捕获或官方账号服务行为。需要这些结果时，在安装包确认后分别手动验证。

Actions 分别测试 Release 和 ReleaseNoReflection，测试通过后制作两个 NativeAOT 包。工作流使用只读仓库权限，只有手动发布作业需要写入 Release。构建产物和测试报告保留 14 天。Action 参考：[checkout](https://github.com/actions/checkout)、[setup-dotnet](https://github.com/actions/setup-dotnet)、[upload-artifact](https://github.com/actions/upload-artifact)。

每个 Actions 发布产物只包含一份可导入 ZIP；不上传展开的发布目录或 Indexer 的中间归档。PDB 调试符号留在本机编译目录，不进入安装包。安装包保留自包含 .NET/WPF 运行时。
