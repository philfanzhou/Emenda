# Emenda issues 执行状态

## 最终关闭回执

2026-11-22 已按用户本次明确授权，逐项补充真实交付/验证依据并关闭 Emenda #18、#19、#20、#21、#22，全部为 `closed / completed`。通过独立 GitHub 连接器重新读取五项确认一致；实时仓库开放事项接口返回 `[]`，`open_issues_count=0`，即 Emenda 当前开放 issues 和 PR 均为 0。

关闭身份为既有协作者 `kingus188`。只读核验确认该身份已有 `push` / `triage` 及相应 `repo` 认证，使用现有授权完成关闭；没有新增成员、扩大 token scope 或修改 App 安装权限。此前连接器的 403 已不再阻塞这批事项的操作，但没有声称修改了连接器本身的安装权限。

完整关闭时间、逐项验证和 GitHub 证据评论见 [关闭回执 JSON](2026-11-22-issue-closure-receipt.json)。本轮约定的实现、合入、测试部署、门户及历史变更记录验收、事项关闭现均已完成。以下曾标记 open/403 的段落为历史过程，不代表当前状态。

## 最新远端状态核对

本轮约定的实现、合入、测试部署及门户验收均已完成，包括历史版本变更记录。仓库事项的关闭状态尚未全部完成：实时查询确认 Emenda #18、#19、#20、#21、#22 仍为 open。此前关闭接口返回 `403 Resource not accessible by integration`，没有绕过拒绝，因此不能把功能完成说成 issues 已关闭。

GitHub 实时回读确认 Emenda PR #24、AI Tutor PR #31 均为 `closed / merged=true`。门户 PR328 head `d0f263aa...` 是 master 合入提交 `947aa1c5...` 的实际第二父提交，代码确已合入；远端 master 当前为历史查看修复 `d525cb1d...`。Gitee PR328 页面/API 状态本次无法核实（未认证 API 返回 404），不据此断言该 PR 页面已关闭，也不把代码合入状态与页面状态混用。

## 历史查看已修复并完成部署验收

用户选择“展示可读的版本变更记录，消除退役快照错误”。修复 `d525cb1dd07f9a57ea214f682e3464d95c34bd03` 已合入 Gitee master 并部署测试机两个有效 Web 门户；API 和 Emenda/Tutor 保持原已验收版本。29 项前端检查、独立审查、两端构建均通过。真实学生/教师浏览器均成功查看 v1/v2 变更记录，无退役快照请求、无写入、无错误；返回当前版本恢复 33 题及原已确认状态，原图读取成功，数据库版本/题目标识/审核回读一致。详见 [历史查看最终验收](2026-11-22-history-records-acceptance.md)。此前“历史查看不可用”的附加限制已解决；下文旧版本交付描述保留为历史记录。

## Portal deployment and current-version acceptance completed

Four active learner/teacher containers now run source revision `947aa1c5ff1fb319efd6183f42f77dd309e4a728`. APIs `/health` and both web roots returned 200. Readback confirmed original environment, mounts, ports and network configuration unchanged, with the required `EmendaService__CallerRevisionsEnabled=true` added to both APIs. Emenda/Tutor and retired assistant containers were not modified.

Real browser acceptance passed: original version 1 displayed all 40 questions (10 incorrect, 30 correct); deliberate synthetic removal created a 32-question version 2; the learner framed question 33 on original page 2 and saved through the UI, yielding version 3 with 33 questions and focus on the new UUID. All 32 siblings retained their UUIDs, item IDs and complete question payloads. The prior confirmed sibling stayed confirmed. The added question remained pending after save, then one teacher confirmation changed it to confirmed and removed its confirmation button. Student page reload and PostgreSQL independently read back confirmation. This was a deliberately seeded omission, not a naturally missed model result.

JSON receipt: `Emenda/docs/operations/acceptance-2026-11-22/portal-947aa1c5-readback.json`. An additional historical-v1 lookup after the deliberate removal returned unavailable; this does not pass historical snapshot acceptance, and is retained as a separate observation.

## Portal master delivery update

Gitee `master` now contains merge commit `947aa1c5ff1fb319efd6183f42f77dd309e4a728`, read back from the remote after a normal non-force push. It integrates PR328 on `91a5892d0467fe349403aa68ef61be95f390cd01` while retaining the standalone Assistant Portal retirement. The original dirty working tree was preserved.

Both active frontend builds and both backend Release builds passed; final backend builds reported zero warnings/errors. Final full learner tests: 1041 passed; teacher: 479 passed. Shared client: 245; review: 61; auth: 440; upload: 69; Node workspace: 28, all passed. The shared test-host missing HttpClient factory and empty DELETE header assumption were corrected without weakening replay/write safeguards.

Exact source archive: `/tmp/Ruoyu.Study-master-947aa1c5-20261122.tar.gz`, SHA256 `05cbefcdbdfb74053dedc603608749919a980402961197e3383baf2d9c5ea65a`. Independent integration clone: `/tmp/ruoyu-pr328-master-delivery-20261122`. Four versioned portal images are deployed and current-version browser acceptance passed as detailed above. Existing Emenda/Tutor and retired assistant containers remain unchanged.

Emenda [PR #24](https://github.com/philfanzhou/Emenda/pull/24) 已合并至 main：`cd5f5500979ecd474428c0ccef5bf3b84a20e70f`。最终 head `6c17e9be756e8dafd884a677a85a48637c4e6653` 的 5 项必需检查全部通过，包含容器镜像构建。

配套 AI Tutor [PR #31](https://github.com/kingus188/ai-tutor/pull/31) 已通过 CI 并合并至 main：`00d47d3e0ecacdd327ac88e6a12695828acaef08`。

| Issue | 实际状态 |
|---|---|
| #18 自然语言候选、细化、显式采纳 | 服务端与配套协议已合并，相关回归通过；关闭操作被 GitHub 集成权限拒绝，仍为 open |
| #19 原题补图与续审 | 服务端、版本化契约与 HTTP 回归已合并；关闭操作被 GitHub 集成权限拒绝，仍为 open |
| #20 单题局部修订 | 服务端、机器契约、跨学科和权限回归已合并；关闭操作被 GitHub 集成权限拒绝，仍为 open |
| #21 当前裁图与持久回收 | 已发布 RC 并部署测试；获批唯一任务迁移与 27 个内容版本实际回收已验收，保护图像和既有数据校验一致；未执行 issue 关闭 |
| #22 解除整卷 32 题限制 | 已合入并部署有效两端；40 题真实门户读取、人工准备 32 题后原图补第 33 题、单次确认及数据库回读全部通过；历史变更记录也已由 `d525cb1d` 修复部署并验收；issue 仍 open |

关闭前三项的接口返回 `403 Resource not accessible by integration`。没有通过替换工具绕过该拒绝；需要具备 issue 写权限的维护者关闭，或恢复此集成的对应权限。当前没有宣称任何 issue 已关闭。

实现、115 项专项检查、完整回归限制及两轮审查见 [实现记录](2026-11-22-question-corrections.md)。最终容器构建遗漏的 v2 JSON 已补入 Dockerfile 和最小源码包清单，远程容器检查已通过。

用户已批准清单内测试迁移、回收及合成验收学生的真实浏览器测试。Emenda `v0.1.1-rc.8`、AI Tutor `v0.1.9-rc.3` 均已发布并以不可变镜像 digest 部署至 `192.168.31.5`，健康检查通过。清单内任务 30 张原图和 3 张当前错题图 SHA256 保持一致，27 个非错题裁图及其内容版本（273,274 字节）已实际回收，既有 71 条错题与 0 条练习的受保护数据指纹一致。生产未操作。

通过真实登录门户提交的两页合成 40 题样本已完整识别：10 错、30 对，答案、判定和裁图数逐题符合预期。首次遇到的 `EMENDA_INVALID_RESPONSE` 已通过门户集成修复。用户批准的部署和当前版本三项门户验收已完成；现行 master 已退役独立助教门户，因此只更新学生、教师四个容器，未恢复助教端。历史版本查看不可用另列已知限制，不能据本次主流程通过宣称历史快照能力也已通过。

完整镜像标识、迁移前后对账、恢复材料和现场阻塞见 [测试迁移与门户验收回执](2026-11-22-test-cutover-result.md)，对应 [已合入门户修复](https://gitee.com/philfanzhou/Ruoyu.Study/commit/947aa1c5ff1fb319efd6183f42f77dd309e4a728) 和实际 40 题比较结果。准备阶段的过时预览补丁已清理。

## 历史推进记录（已由上述交付结论取代）

用户已明确批准核验整合 PR328、评审后合入 Gitee master、测试机三端 BFF/前端部署及补第 33 题和一次确认；上文的“门户范围待授权”已被此授权取代。整合源码保留在 `/tmp/Ruoyu.Study-pr328-integrated-20261122`，原脏工作树保留。三端后端和前端六项目构建均通过，HTTP client 248 项、其余 Emenda 专项检查通过。

精确 master `806589f6291f4dd41a366e9e1dafae736feebea8` 的共享审核套件实跑 16 pass / 42 fail，整合版 19 pass / 42 fail；42 个失败测试名及完整错误消息一致，无新增失败。未将既有失败隐藏或宣称全套通过。详见 [审查与构建报告](2026-11-22-portal-integration-review-build.md)。

尚未合入或部署。用户已纠正发布路径：各模块使用各自仓库，门户沿用已有 Gitee 交付方式；新建 GitHub 仓库不再是合入阻塞。

2026-11-22 最新合入核对：本地 `origin/master` 为 `91a5892d0467fe349403aa68ef61be95f390cd01`，其中 !333 已退役独立 Assistant Portal。PR328 head `d0f263aa9fd4cc4ac04ead303348e22b66a6ff7c` 尚非该 master 的祖先。执行 `git merge-tree --write-tree origin/master d0f263aa9fd4cc4ac04ead303348e22b66a6ff7c` 返回 1，确认三个冲突：已删除的助教前端规格被 PR 修改、共享 `EmendaExtraction.md` 内容冲突、`emenda-adjustments.browser.cjs` 内容冲突。尚未解决或生成可交付提交；合并结果中的冲突树不能发布。后续须保留 !333 的退役决定，将旧整合归档接续到新 master，并在实际最终候选上重新验证。原脏工作树保留，本阶段未推送或操作测试容器。40 题门户读取、32→33 补题和一次确认仍未验收。
