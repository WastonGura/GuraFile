# GuraFile

GuraFile 是一个 Windows 优先的标签式文件管理器。它保留真实文件系统作为唯一文件来源，在本地建立索引，让你用标签而不是文件夹层级整理和查找文件。

## v0.3.1 Alpha

本版为回收站安全补丁与测试隔离修复版本，在 v0.3.0 基础上强制回收站软删除语义并完善端到端验证：

- 修复回收站删除安全契约（#47）：删除操作显式使用 `FOFX_RECYCLEONDELETE` 与 `FOF_ALLOWUNDO`，当回收站不可用或 Shell 拒绝时直接失败，严禁降级为永久删除；
- 完善测试隔离与业务链验收（#48）：建立隔离的真实回收站端到端测试与自身产物精确清理机制，补齐“复制/移动/重命名/软删除/重启恢复”全业务链验证；
- 建立基于 Windows STA `IFileOperation` 的安全文件操作执行器，支持复制、移动与单文件重命名；
- 提供“自动重命名”、“覆盖”和“跳过”三种冲突处理策略，支持操作进度与随时取消；
- 同卷移动与重命名保留稳定文件身份与已有用户标签；复制与跨卷移动继承用户标签并重算自动标签；
- 支持与 Windows 资源管理器双向复制/剪切/粘贴（标准 `CF_HDROP` / `Preferred DropEffect` 剪贴板）；
- 支持从资源管理器拖入文件复制到管理根目录，以及在应用内拖放移动文件；
- 接入常用文件操作快捷键（`Ctrl+C`、`Ctrl+X`、`Ctrl+V`、`Ctrl+A`、`F2`、`F5`、`Delete`），文本编辑时不拦截快捷键；
- 完善右侧结构化文件详情面板与在线状态，提供“复制路径”入口。

这是 Alpha，不建议把 SQLite 索引数据库本身作为唯一备份。重要整理结果请定期使用“导出备份”。

## 安装与运行

1. 从 GitHub Releases 下载 `GuraFile-v0.3.1-win-x64.zip` 和对应的 `.sha256` 文件。
2. 校验压缩包 SHA-256 后解压到可写目录。
3. 运行 `GuraFile.exe`。应用为未签名预览包，Windows 可能显示 SmartScreen 提示。

系统要求：x64 Windows 10 版本 1809（build 17763）或更高版本。发布包为 unpackaged、自包含部署，不要求单独安装 .NET 或 Windows App Runtime。

## 基本使用

1. 在左侧添加管理根目录；应用会立即在后台建立索引，必要时也可选择“扫描”。
2. 在中间文件列表选择一个或多个文件。
3. 在左侧创建并选择标签，然后选择“贴到文件”。
4. 开启“按所选标签筛选”，选择“满足任一标签”或“满足全部标签”。
5. 在工具栏或快捷键执行“复制”、“剪切”、“粘贴到…”、“移动到…”、“重命名”或“删除”。
6. 在右侧结构化面板查看属性、在线状态、稳定身份状态与标签，使用“打开”、“在资源管理器中显示”或“复制路径”。
7. 使用“导出备份”保存用户标签；恢复时先扫描目标文件，再选择“导入备份”。

索引保存在 `%LOCALAPPDATA%\GuraFile\index.db`。移除管理根目录只会删除 GuraFile 索引，不会删除真实文件。

## 备份与升级

- JSON 备份格式为 `GuraFile.UserTags` v1，只包含用户标签和用户关系；自动标签与可重建元数据不进入备份。
- stable 身份只按稳定文件身份恢复；path 降级记录只匹配在线 path 身份，不会把标签猜测绑定到同路径替换文件。
- 原生兼容 v0.2.0 schema v5 数据库；升级前建议先导出标签备份。
- 单个 JSON 备份上限为 64 MB；超限时导出会明确失败，不会生成无法重新导入的文件。

## 已知限制

- 实时监听基于 FileSystemWatcher，正常负载目标为 2 秒内更新，并由启动、错误和离线恢复扫描兜底；本版不使用 NTFS USN Journal。
- 删除操作仅支持删除到 Windows 回收站，不提供永久删除选项；若回收站不可用则拒绝删除。
- 尚未提供图谱；文件—标签可视化将在后续版本实现。
- 尚未提供保存视图、全文检索或跨平台版本。
- 当前仅提供未签名的 Windows x64 便携压缩包。

## 隐私与数据边界

GuraFile 的索引和标签保存在本机，不上传文件内容。真实文件始终位于原目录；当前版本只会读取元数据和最多 32 字节文件头、打开文件、请求资源管理器定位，以及修改本地索引和用户选择的 JSON 备份。

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
.\scripts\PackageRelease.ps1 -Version 0.3.1
```

项目按 GitHub Issue、独立 worktree、测试先行、独立审查、PR 和受保护 `main` 分支交付。第三方组件及许可见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)，版本变化见 [CHANGELOG.md](CHANGELOG.md)。
