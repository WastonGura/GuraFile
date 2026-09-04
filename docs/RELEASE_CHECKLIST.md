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
