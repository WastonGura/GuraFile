# GuraFile

GuraFile 是一个 Windows 优先的标签式文件管理器。它保留真实文件系统作为唯一文件来源，在本地建立索引，让你用标签而不是文件夹层级整理和查找文件。

## v0.1.0 Technical Preview

首个公开预览版已经支持：

- 添加一个或多个管理根目录并手动差异扫描；
- 使用稳定 Windows 文件身份跟踪同卷重命名和移动；
- 在虚拟化列表中按名称、路径、扩展名和元数据筛选、排序；
- 创建、重命名和删除用户标签，为单个或多个文件批量贴标；
- 按任一标签或全部标签组合筛选；
- 使用系统默认应用打开文件，或在资源管理器中定位文件；
- 将用户标签与关系导出为版本化 JSON，并恢复到重新扫描后的索引。

这是 Technical Preview，不建议把 SQLite 索引数据库本身作为唯一备份。重要整理结果请定期使用“导出备份”。

## 安装与运行

1. 从 GitHub Releases 下载 `GuraFile-v0.1.0-win-x64.zip` 和对应的 `.sha256` 文件。
2. 校验压缩包 SHA-256 后解压到可写目录。
3. 运行 `GuraFile.exe`。应用为未签名预览包，Windows 可能显示 SmartScreen 提示。

系统要求：x64 Windows 10 版本 1809（build 17763）或更高版本。发布包为 unpackaged、自包含部署，不要求单独安装 .NET 或 Windows App Runtime。

## 基本使用

1. 在左侧添加管理根目录并选择“扫描”。
2. 在中间文件列表选择一个或多个文件。
3. 在左侧创建并选择标签，然后选择“贴到文件”。
4. 开启“按所选标签筛选”，选择“满足任一标签”或“满足全部标签”。
5. 在右侧使用“打开”或“在资源管理器中显示”。
6. 使用“导出备份”保存用户标签；恢复时先扫描目标文件，再选择“导入备份”。

索引保存在 `%LOCALAPPDATA%\GuraFile\index.db`。移除管理根目录只会删除 GuraFile 索引，不会删除真实文件。

## 备份与升级

- JSON 备份格式为 `GuraFile.UserTags` v1，只包含用户标签和用户关系；自动标签与可重建元数据不进入备份。
- stable 身份只按稳定文件身份恢复；path 降级记录只匹配在线 path 身份，不会把标签猜测绑定到同路径替换文件。
- 数据库 schema 会在启动时自动向前迁移。升级前建议先导出标签；升级后的数据库不能由不支持该 schema 的旧版本打开。
- 单个 JSON 备份上限为 64 MB；超限时导出会明确失败，不会生成无法重新导入的文件。

## 已知限制

- 没有实时监听；磁盘变化后需要手动重新扫描。
- 不提供复制、移动、重命名或删除等文件写操作。
- 尚未提供图谱；文件—标签可视化将在后续版本实现。
- 尚未提供自动类型标签、保存视图、全文检索或跨平台版本。
- path 身份是稳定身份不可用时的保守降级，外部重命名后可能无法自动延续节点。
- 当前仅提供未签名的 Windows x64 便携压缩包。

## 隐私与数据边界

GuraFile 的索引和标签保存在本机，不上传文件内容。真实文件始终位于原目录；当前版本只会读取元数据、打开文件、请求资源管理器定位，以及修改本地索引和用户选择的 JSON 备份。

## 开发与验证

需要 .NET SDK 10.0.400 或更高版本。在仓库根目录使用 PowerShell 7：

```powershell
dotnet restore .\GuraFile.slnx
dotnet build .\GuraFile.slnx --configuration Release --no-restore
dotnet test .\tests\GuraFile.Tests\GuraFile.Tests.csproj --configuration Release --no-build
.\tests\LaunchSmoke.ps1 -Configuration Release -RuntimeIdentifier win-x64
```

生成可发布包：

```powershell
.\scripts\PackageRelease.ps1 -Version 0.1.0
```

项目按 GitHub Issue、独立 worktree、测试先行、独立审查、PR 和受保护 `main` 分支交付。第三方组件及许可见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)，版本变化见 [CHANGELOG.md](CHANGELOG.md)。
