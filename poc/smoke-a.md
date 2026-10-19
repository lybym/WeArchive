# POC Smoke A 产物（batch `poc-20261018`）

**POC 声明**：本文件是批次 `poc-20261018` 的 Phase 1 冒烟产物（Proof of Concept），仅用于验证 `Step 03 → Step 04(APPROVE) → Step 08 Gate 校验` 的串行 Issue 流水线链路。**本分支与其 PR 绝不合并，不触碰 `main`。**

## 元信息

- 对应 Issue：#126（POC Smoke A: review carrier APPROVE path）
- Issue URL：<https://github.com/lybym/WeArchive/issues/126>
- Batch ID：`poc-20261018`
- Stage Attempt：`iss-126|step-03|attempt-1`
- 基线分支：`poc/team-issue/poc-20261018/base`（BASE_SHA `5f494f38a20140921b0e18ee3a5c393334cafb6f`）
- Feature 分支：`poc/team-issue/poc-20261018/smoke-a`
- 变更范围：仅新增本文件 `poc/smoke-a.md`，不修改任何其他文件
- 执行路径：GitHub API（本机 git https 受 schannel `SEC_E_NO_CREDENTIALS` 限制，按 Phase 0 已验证的 API 路径执行）

## 用途

- 验证 feature 分支从隔离 base 切出、GitHub API 提交、显式 `--base` 创建 PR 与 CI 的完整通路。
- 作为独立 Review Agent（review carrier）APPROVE 路径的冒烟载体，供 Step 04 / Step 08 Gate 校验使用。

Refs #126
