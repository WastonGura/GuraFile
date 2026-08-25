# Agent workflow

1. 开始工作前读取 `agent_memory/README.md`、`CONTEXT.md`、`PROGRESS.md` 和 `DECISIONS.md`；目录不存在时先向维护者确认当前上下文。
2. 先只读了解仓库结构，再根据 GitHub Issue 创建独立 worktree；不要直接在主工作区实现 Issue。
3. 实现 Agent 采用 TDD：先留下会失败的最小检查，再实现并验证。
4. 使用不同 Agent 独立审查正确性、回归、数据安全和缺失测试。
5. 审查通过后提交 PR；CI 通过后才合并。
6. 合并后更新 `agent_memory/PROGRESS.md`、必要的决策记录和项目结构。
7. `agent_memory/` 与 `docs/begin/` 是本地上下文，不得强制添加或提交。
8. 采用最小实现：复用平台和现有代码，不为未排期需求增加抽象、服务或依赖。
