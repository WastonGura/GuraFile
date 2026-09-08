# v0.5.1 发布验收

验收日期：2026-09-08

支持平台：Windows 10 1809 及以上，x64

## 前置与 Issue 交付验证

- [x] #96 修复崩溃恢复时潜在标签丢失与未决意图判定已合并并通过 CI。
- [x] #97 防范标签 ID 复用并保持失效视图安全语义已合并并通过 CI。
- [x] #98 统一诊断脱敏一致性并消除 UI 阻塞 I/O 已合并并通过 CI。
- [x] #99 优化搜索子串匹配语义并完善冷启动与端到端严谨压测已实现并通过验证。

## 搜索子串匹配与性能基准

- [x] 复合搜索策略：FTS5 与精准 LIKE 子串复合 UNION 查询，特殊通配符（%, _, \）严格转义，支持多词元 AND 语义。
- [x] 驼峰子串检索：`ProjectAlpha.cs` 能够被 `Alpha` 以及 `Project` 检索命中。
- [x] 中文子串检索：`2026年财务报告_Q1.xlsx` 能够被 `财务` 以及 `报告`、`财务 报告` 精准命中。
- [x] 独立进程真实冷启动：连续 3 次启动至主窗口可见并响应，实测耗时约为 450 ~ 496 ms，均在 3.0 秒门禁以内。
- [x] 滚动备份下批量打标：启用 `RollingTagBackupService` 自动备份时，1,000 文件批量打标实测连续 3 次耗时 12 ~ 31 ms，均在 2.0 秒门禁以内。
- [x] 十万文件检索性能全面覆盖：宽匹配子串搜索、纯标签筛选（Any 和 All）、搜索+标签组合筛选，首屏响应均低于 200 ms。
- [x] 图谱首帧基线：300 节点离线 Cytoscape.js 图谱首帧实测约 450 ~ 490 ms，保持低于 1 秒基线。

## 本地构建、测试与候选包

- [x] 产品版本 `0.5.1`、程序集版本和文件版本 `0.5.1.0`、打包默认值及文档当前版本一致。
- [x] Release x64 自包含构建成功，0 警告、0 错误。
- [x] Release 自动化测试全部 100% 通过（无失败、无跳过）。
- [x] Release 构建输出可见窗口启动冒烟通过（`tests\LaunchSmoke.ps1`）。
- [x] `PackageRelease.ps1 -Version 0.5.1` 成功生成本地候选 ZIP 与 checksum。
- [x] ZIP 包含 `App.xbf`、`MainWindow.xbf`、`GuraFile.pri`、四项离线图谱资产、`README.md`、`CHANGELOG.md`、`THIRD_PARTY_NOTICES.md` 及许可证。
- [x] 包内 `GuraFile.exe` 文件版本为 `0.5.1.0`。
- [x] 从 ZIP 解压后的包内 `GuraFile.exe` 可见窗口启动且响应正常，退出后无残留进程。
- [x] `git diff --check` 通过。

## 主 Agent 后续发布步骤

本实现提交不创建 PR、不发布 Release。独立审查、Issue #99 PR 与 CI、合并后从干净 remote-main 重建、最终资产上传与回下载复验及 Milestone 关闭由主 Agent 后续完成；最终发布 ZIP 的 SHA-256 应以合并后干净构建结果为准。

# v0.5.0 发布验收

验收日期：2026-09-06

支持平台：Windows 10 1809 及以上，x64

## 前置与 Issue 交付验证

- [x] #71 用户标签自动滚动备份与恢复已合并并通过 CI。
- [x] #72 历史数据库逐级迁移矩阵已合并并通过 CI。
- [x] #73 数据库损坏检测与安全重建已合并并通过 CI。
- [x] #74 本地诊断日志与用户主动导出已合并并通过 CI。
- [x] #75 网络盘、可移动介质与重解析点降级状态已合并并通过 CI。
- [x] #76 崩溃后恢复未完成扫描已合并并通过 CI。
- [x] #77 防止崩溃后重复执行文件写操作已合并并通过 CI。
- [x] #78 十万文件扫描与冷启动性能优化已合并并通过 CI。
- [x] #79 FTS5 搜索与千文件批量标签优化已合并并通过 CI。
- [x] #80 已保存筛选视图持久化与失效防护已合并并通过 CI。
- [x] #81 状态栏、焦点管理、基础无障碍与最小设置页已合并并通过 CI。
- [x] #82 完成 Beta 端到端全链路业务验收、发布元数据与打包准备。

## 端到端业务链与数据安全

- [x] 完整端到端全链路验收：创建根目录 -> 扫描 -> 贴标 -> FTS5 搜索 -> 保存视图 -> 复制/重命名/移动 -> 回收站软删除 -> 崩溃意图对账 -> 重启恢复验证通过。
- [x] 数据库逐级迁移端到端验收：v1 (v0.1.0) 数据库平滑升级至 v10 (v0.5.0)，历史文件、用户标签、外键完整性无损，FTS5 检索正常。
- [x] 数据库损坏与自愈重建端到端验收：损坏数据库安全隔离至 `.corrupt_timestamp.bak`，磁盘重扫与滚动备份恢复用户标签，准确报告未匹配文件与标签冲突。
- [x] 崩溃恢复写操作安全契约：未完成意图对账后仅对齐磁盘与索引，绝不重放物理 Shell 写操作。
- [x] 回收站软删除安全契约：删除强制使用 `FOFX_RECYCLEONDELETE` + `FOF_ALLOWUNDO`，严禁降级为永久删除。

## 本地构建、测试与候选包

- [x] 产品版本 `0.5.0`、程序集版本和文件版本 `0.5.0.0`、打包默认值及文档当前版本一致。
- [x] Release x64 自包含构建成功，0 警告、0 错误。
- [x] Release 自动化测试全部 100% 通过（无失败、无跳过）。
- [x] Release 构建输出可见窗口启动冒烟通过（`tests\LaunchSmoke.ps1`）。
- [x] `PackageRelease.ps1 -Version 0.5.0` 成功生成本地候选 ZIP 与 checksum。
- [x] ZIP 包含 `App.xbf`、`MainWindow.xbf`、`GuraFile.pri`、四项离线图谱资产、`README.md`、`CHANGELOG.md`、`THIRD_PARTY_NOTICES.md` 及许可证。
- [x] 包内 `GuraFile.exe` 文件版本为 `0.5.0.0`。
- [x] 从 ZIP 解压后的包内 `GuraFile.exe` 可见窗口启动且响应正常，退出后无残留进程。
- [x] `git diff --check` 通过。

## 主 Agent 后续发布步骤

本实现提交不创建 PR、不发布 Release。独立审查、Issue #82 PR 与 CI、合并后从干净 remote-main 重建、最终资产上传与回下载复验及 Milestone 关闭由主 Agent 后续完成；最终发布 ZIP 的 SHA-256 应以合并后干净构建结果为准。

# v0.4.1 发布验收

验收日期：2026-09-04

支持平台：Windows 10 1809 及以上，x64

本次性能目标设备：Windows 10.0.26200.0、AMD Ryzen 7 7735H、WebView2 152.0.4191.62、.NET 10.0.11、1280×800 可见窗口。结果只代表此目标 x64 设备；其他受支持设备需单独运行真实 WebView2 harness。

## 前置与问题更正

- [x] #67 已独立审查并合并为 `baad354`，PR #69 与合并后 `main` 的 `verify` CI 均成功。
- [x] 更正 v0.4.0 验收结论：真实 WebView2/Cytoscape.js `layoutstop` 三次 JS 首帧为 1401.10 / 1404.30 / 1424.60 ms，未达到三次均小于 1000 ms 的门禁。
- [x] v0.4.1 复用现有 `cose` 布局，仅由 #67 将 `numIter` 调整为 400，并加入不访问用户 `index.db` 的隔离真实 WebView2 harness；#68 未修改运行时代码。
- [x] 未修改或删除既有 v0.4.0 tag、Release 或资产。

## 真实图谱首帧

- [x] 源码与 Release 输出中的 `index.html`、`cytoscape.min.js`、`graph.css`、`graph.js` 四项 SHA-256 一致。
- [x] 第 1 次：JS 668.1 ms，Host 729.88 ms，310 节点 / 300 边。
- [x] 第 2 次：JS 670.0 ms，Host 725.11 ms，310 节点 / 300 边。
- [x] 第 3 次：JS 669.4 ms，Host 721.67 ms，310 节点 / 300 边。
- [x] 三次均在可见窗口收到真实 `layoutstop` 后的 `firstFrameRendered`，零远程请求，且 JS / Host 均小于 1000 ms。
- [x] 每次使用独立 WebView2 profile，运行后自动清理 profile 且无 harness 残留进程。

## 本地构建、测试与候选包

- [x] 产品版本 `0.4.1`、程序集版本和文件版本 `0.4.1.0`、打包默认值及文档当前版本一致。
- [x] Release x64 自包含构建成功，0 警告、0 错误。
- [x] Release 自动化测试 292/292 通过。
- [x] Release 构建输出可见窗口启动、响应与进程清理通过。
- [x] `PackageRelease.ps1 -Version 0.4.1` 成功生成本地候选 ZIP 与 checksum。
- [x] 本地候选 ZIP SHA-256 为 `c1a6eda3e87dc0bdd0fad590c97d4457a4745f59a6c88c581f5f00632a36e730`，与 `.sha256` 内容一致。
- [x] ZIP 共 539 个条目，包含 `App.xbf`、`MainWindow.xbf`、`GuraFile.pri`、四项离线图谱资产、`THIRD_PARTY_NOTICES.md` 及 7 个许可文件；图谱资产 SHA-256 与源码一致。
- [x] 包内 `GuraFile.exe` 文件版本为 `0.4.1.0`，产品版本为 `0.4.1+baad354aeef6f360e74dd3da0cfc37371aad1c6a`。
- [x] 从 ZIP 解压后的包内 `GuraFile.exe` 可见窗口启动且响应正常，退出后无残留进程。
- [x] `git diff --check` 通过。

## 主 Agent 后续发布步骤

本实现提交不创建 PR、不发布 Release。独立审查、Issue #68 PR 与 CI、合并后从干净 remote-main 重建、最终资产上传与回下载复验、v0.4.0 升级提示及 Milestone 关闭由主 Agent 后续完成；最终发布 ZIP 的 SHA-256 应以合并后干净构建结果为准。
