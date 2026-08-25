# GuraFile

GuraFile 是一个 Windows 优先的标签式文件管理器。它保留真实文件系统作为唯一文件来源，通过本地索引、用户标签、自动类型标签和关系图谱提供不同于文件夹树的组织方式。

## 当前状态

项目处于 `v0.1.0` Technical Preview 开发阶段。首个版本聚焦文件索引、手动标签、列表搜索和安全的数据导出，不以替代 Windows 资源管理器为目标。

## 核心原则

- 原始文件始终保存在用户选择的位置；
- 用户标签与自动标签分开保存；
- 文件监听只作为变更提示，最终状态以磁盘回读和差异扫描为准；
- 图谱使用文件—标签二部图，避免文件两两连线失控；
- 数据安全、可恢复性和可验证性优先于功能数量。

## 开发

开发以 GitHub Issue 为交付单位，使用独立 worktree、测试先行、独立代码审查和受保护的 `main` 分支。

### 前置条件

- Windows 10 版本 1809（build 17763）或更高版本；
- .NET SDK 10.0.400 或更高版本；
- 可访问 NuGet.org 以还原项目依赖。

应用使用 Windows App SDK 2.4.0，并以 unpackaged、自包含方式运行；不需要全局安装 WinUI 项目模板或 Windows App Runtime。

### 还原、构建与测试

在干净 clone 的仓库根目录使用 PowerShell 7：

```powershell
dotnet restore .\GuraFile.slnx
dotnet build .\GuraFile.slnx --configuration Debug --no-restore
dotnet test .\tests\GuraFile.Tests\GuraFile.Tests.csproj --configuration Debug --no-build
```

### 运行

```powershell
dotnet run --project .\src\GuraFile\GuraFile.csproj
```

应用启动后应显示标题为 `GuraFile` 的最小主窗口。该 UI 启动步骤需要在交互式 Windows 桌面手动确认；自动化 smoke test 不创建窗口。
