# GuraFile

GuraFile 是一个 Windows 优先的标签式文件管理器。它保留真实文件系统作为唯一文件来源，在本地建立索引，让你用标签而不是文件夹层级整理和查找文件。

## v0.4.0 Graph Preview

本版为图谱预览（Graph Preview）版本，在 v0.3 系列稳定文件操作基础上引入完全离线的本地关系图谱与批量交互：

- 本地图谱预览（#57, #58）：基于 Cytoscape.js 3.30.2 与 WinUI 3 WebView2 提供完全离线的文件—标签受限二部图可视化，断网完全可用；
- 安全沙箱与三重拦截（#58）：本地虚拟域名 `graph.gurafile.local`，实施外部导航、新窗口弹窗与文件下载三重拦截，严格 CSP 杜绝脚本注入；
- 列表与图谱筛选选择双向联动（#59）：两视图共享同一筛选快照，点击高亮同步刷新右侧详情，双击通过内存安全验证执行 Shell 打开；
- 图谱框选与批量打标（#60）：支持鼠标框选多个文件节点，展示共有用户标签并支持单事务批量打标/去标，自动标签严格只读保护；
- 300 节点性能保护（#57, #58, #61）：图谱预览设定 300 个文件节点上限，超限明确提示收窄筛选条件，避免界面卡顿；
- 保持基于 Windows STA `IFileOperation` 的安全文件操作（复制、移动、重命名、强制回收站软删除 `FOFX_RECYCLEONDELETE` + `FOF_ALLOWUNDO`）与标签继承。

这是 Preview 版本，不建议把 SQLite 索引数据库本身作为唯一备份。重要整理结果请定期使用“导出备份”。

## 安装与运行

1. 从 GitHub Releases 下载 `GuraFile-v0.4.0-win-x64.zip` 和对应的 `.sha256` 文件。
2. 校验压缩包 SHA-256 后解压到可写目录。
3. 运行 `GuraFile.exe`。应用为未签名预览包，Windows 可能显示 SmartScreen 提示。

系统要求：x64 Windows 10 版本 1809（build 17763）或更高版本。发布包为 unpackaged、自包含部署，不要求单独安装 .NET 或 Windows App Runtime。

## 基本使用

1. 在左侧添加管理根目录；应用会立即在后台建立索引，必要时也可选择“扫描”。
2. 在中间文件列表选择一个或多个文件。
3. 在左侧创建并选择标签，然后选择“贴到文件”。
4. 开启“按所选标签筛选”，选择“满足任一标签”或“满足全部标签”。
5. 在工具栏切换“列表”与“图谱”视图；在图谱中可拖拽、缩放、框选文件节点批量打标，或双击安全打开。
6. 在工具栏或快捷键执行“复制”、“剪切”、“粘贴到…”、“移动到…”、“重命名”或“删除”。
7. 在右侧结构化面板查看属性、在线状态、稳定身份状态与标签，使用“打开”、“在资源管理器中显示”或“复制路径”。
8. 使用“导出备份”保存用户标签；恢复时先扫描目标文件，再选择“导入备份”。

索引保存在 `%LOCALAPPDATA%\GuraFile\index.db`。移除管理根目录只会删除 GuraFile 索引，不会删除真实文件。

## 备份与升级

- JSON 备份格式为 `GuraFile.UserTags` v1，只包含用户标签和用户关系；自动标签与可重建元数据不进入备份。
- stable 身份只按稳定文件身份恢复；path 降级记录只匹配在线 path 身份，不会把标签猜测绑定到同路径替换文件。
- 原生兼容 v0.2.0、v0.3.x schema v5 数据库；升级前建议先导出标签备份。
- 单个 JSON 备份上限为 64 MB；超限时导出会明确失败，不会生成无法重新导入的文件。

## 已知限制

- 实时监听基于 FileSystemWatcher，正常负载目标为 2 秒内更新，并由启动、错误和离线恢复扫描兜底；本版不使用 NTFS USN Journal。
- 删除操作仅支持删除到 Windows 回收站，不提供永久删除选项；若回收站不可用则拒绝删除。
- 图谱预览功能设定 300 个文件节点上限以确保交互流畅；超过 300 个文件时图谱会提示收窄筛选条件，不截断渲染。
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
.\scripts\PackageRelease.ps1 -Version 0.4.0
```

项目按 GitHub Issue、独立 worktree、测试先行、独立审查、PR 和受保护 `main` 分支交付。第三方组件及许可见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)，版本变化见 [CHANGELOG.md](CHANGELOG.md)。
