# 2026-11-22 测试迁移与门户验收回执

事项关闭补记：用户明确要求解决权限路径并关闭已完成事项后，已由具备仓库授权的既有身份 `kingus188` 逐项添加验证依据，将 #18–#22 关闭为 `completed`，独立回读确认。当前 Emenda 开放 issues/PR 为 0；详见 [关闭回执](2026-11-22-issue-closure-receipt.json)。下文此前关于未关闭/403 的叙述保留为当时记录。

后续历史查看修复已完成：用户明确选定版本变更记录口径后，提交 `d525cb1d` 已合入并部署学生/教师 Web，历史 v1/v2 查看、返回当前、原图与审核保持均通过真实门户及数据库回读。详见 [历史查看最终验收](2026-11-22-history-records-acceptance.md)。本记录下文的历史查看不可用描述，是该修复之前的观察，已不代表当前状态。

最终核验：约定的 40 题门户读取、人工准备 32 题后原图补第 33 题、一次确认与持久化回读全部通过。主线程再次独立读取数据库，确认任务 `Succeeded` / v3、33 个唯一题目标识、新增题及既有已确认兄弟题均为审核状态 2；四个运行容器 revision 均为 `947aa1c5ff1fb319efd6183f42f77dd309e4a728`，两端 API 健康及 Web 根路径均 HTTP 200。附加发现的旧历史版本查看不可用仍保留为已知限制，本次没有宣称该能力通过。

## Subsequent authorized Portal delivery and acceptance

Gitee master delivery was read back as `947aa1c5ff1fb319efd6183f42f77dd309e4a728`, a merge of PR328 into the assistant-retirement baseline. Only the two active portals were updated. Original dirty source files were preserved in place. No GitHub repository or release was invented.

Both frontend builds and both backend Release builds passed, with final backend builds reporting zero warnings/errors. Full learner tests: 1041 passed; full teacher tests: 479 passed. Shared client/review/auth/upload suites: 245/61/440/69 passed. Workspace Node tests: 28 passed. The missing test-host HttpClient factory and runtime-dependent empty DELETE header assertion were repaired without weakening replay or subsequent-write checks.

The exact source archive is `/tmp/Ruoyu.Study-master-947aa1c5-20261122.tar.gz`, SHA256 `05cbefcdbdfb74053dedc603608749919a980402961197e3383baf2d9c5ea65a`. Server release directory: `/home/zhf/docker/ruoyu/releases/portal-emenda-20261122/`. It holds source, build logs, private pre-change container configuration, rollback image archive, web asset packages, packaging Dockerfile, and the complete deployed image archive `portal-images-947aa1c5.tar`, SHA256 `b2e45f3cfe6790767bca0f22609d713158eec6d8e332fc880067f6c367adf44a`.

Backend images were built on the test host using repository Dockerfiles. Docker Hub token retrieval reset the web Dockerfile builds twice. Web images therefore package the exact locally built frontend outputs from the same committed source onto the host's cached `nginx:1.31.8-alpine3.24`; this packaging file and assets are retained. These are fixed, archived test artifacts, not claimed as registry-published RC images. This follows the user's explicit portal-specific delivery instruction; no existing upstream Emenda/Tutor image was changed.

| Container | Image ID | Revision label |
| --- | --- | --- |
| ruoyu-user-api | `sha256:4394899108f4a1eb1daed9bdefce74f002e7e5a80e0a0eb4cb18ad8b74436d55` | `947aa1c5ff1fb319efd6183f42f77dd309e4a728` |
| ruoyu-teacher-api | `sha256:f36db7436821f4e789676f66d1e6f5ed19dfb15c77647807413e4711c8f78f9e` | same |
| ruoyu-user-web | `sha256:2f6cb9282b085438fc1a81c3fb6aff9975840250796bee9ab3c65f8e9d056ed9` | same |
| ruoyu-teacher-web | `sha256:a5a19f23321326d32a3de0e28c17471fd51a5266b8ecd97319d81c670e91ae1b` | same |

All four were read back running. API health endpoints and both web roots returned 200. All original HostConfig values (including ports, mounts and networks) and original environment entries match the pre-change snapshot. The required `EmendaService__CallerRevisionsEnabled=true` is the sole environment addition to both APIs. Old containers are stopped rollback material, not second running instances. The retired assistant containers were untouched.

The real learner browser displayed 40 questions from the existing job without repeating analysis: 10 incorrect and 30 correct. For the boundary test only, an explicitly described synthetic revision deliberately removed printed questions 2,33,34,35,36,38,39,40, producing version 2 with 32 questions. This is not a claim of natural model omission. The learner selected original page 2, framed question 33 and its photographed answer, entered the original statement and answers, then saved through the UI. Version 3 contains 33 questions; focus returned to the newly returned UUID and the added item remained pending.

All 32 retained question UUIDs, item IDs and question payloads match the pre-setup job. The previously confirmed first question stayed confirmed. One teacher click confirmed the newly added question; its confirmation button disappeared. Reloading the learner page and a separate PostgreSQL query both verified review status 2 for the new item and the original confirmed sibling. PostgreSQL read back `Succeeded`, version 3, 33 active question bindings.

Exact identifiers and numeric source region: [JSON readback](acceptance-2026-11-22/portal-947aa1c5-readback.json). Screenshots: teacher `/Users/haifeng/.t3/userdata/browser-artifacts/browser-screenshot-192-168-31-5-mwrsth40-80b177a5.png`; learner `/Users/haifeng/.t3/userdata/browser-artifacts/browser-screenshot-192-168-31-5-mwrsthk3-018bef78.png`.

Additional observation outside the accepted current-version journey: viewing historical version 1 after deliberate removals returned unavailable. Historical snapshot compatibility has not passed acceptance and remains follow-up work. No issue closure or production acceptance is claimed.

## 结论与授权边界

已按用户明确批准，在 `192.168.31.5` 升级配套 RC，并完成清单内唯一旧上传任务的 schema v20 迁移与回收。生产未操作。全量存量回收开关 `Emenda__Extraction__CleanupLegacyObjects=false` 保持关闭，仅将获批的 27 个对象送入持久回收队列。

真实门户提交的合成 40 题样本已由实际部署的分析器完整识别，逐题答案、判定和裁图数量均符合预期。首次出现的门户 BFF `502 EMENDA_INVALID_RESPONSE` 已修复并部署；后续实际门户读取、人工准备的 32→33 补题、一次确认及独立数据库回读均通过。分析成功与门户验收分别保留证据。

## 已部署制品

发布工作流均已成功；下列 index digest 已固定在启动脚本。运行实例报告的 Linux/amd64 manifest digest 与 registry 中选中的平台 manifest 一致，源码 revision 标签也逐项一致。每项服务只有一个运行实例。

| 服务 | RC / source revision | index digest | Linux/amd64 manifest digest |
|---|---|---|---|
| Emenda | `v0.1.1-rc.8` / `cd5f5500979ecd474428c0ccef5bf3b84a20e70f` | `sha256:e3a11e4ebd8b28027bfff6570ffa7c3c9942cff58d9cb71591e7593dcc16fb12` | `sha256:d5046ffb3ccd02ef48e69712ff53c467a68302ccaf51c33cc5f510409f0f8d00` |
| AI Tutor | `v0.1.9-rc.3` / `00d47d3e0ecacdd327ac88e6a12695828acaef08` | `sha256:c29364b1fa38d00caf6642bbffb73b8b46929a648228e62c500710ff96e88bfb` | `sha256:63b09ad51076306eab1fdb647bc9fb84716c08808b44532165d229f848b7ccd0` |

Emenda repository: `ghcr.io/philfanzhou/emenda`；AI Tutor repository: `ghcr.io/kingus188/ai-tutor`。发布未修改 stable `latest`。Emenda `/health/ready` 的数据库、OSS、StructaDoc 检查全部通过，运行重启次数为 0；AI Tutor `/health` 返回 `ok:true`。原端口、挂载、网络和 DNS 参数保留，运行时秘密未写入此仓库。

## 清单内迁移结果

不可变批准清单：[2026-11-22-legacy-crop-plan.json](2026-11-22-legacy-crop-plan.json)，SHA256 `c808c804b59de0f4f78feaa022a0c31e98dc66e9ee8ee92f66aa12cb3b85d30c`。

任务 `dee1e6af-bd52-44d9-bf2c-d0f8ab6c8687`，版本仍为 `1`。迁移后通过 `ApprovedCropMigration verify`，再开始创建独立的门户验收任务。

| 校验项 | 执行前 | 执行后 |
|---|---:|---:|
| 本任务前缀下的 S3 对象 | 60 | 33 |
| 本任务前缀下的内容字节 | 928,604 | 655,330 |
| 受保护原图 | 30 | 30 |
| 受保护当前错题裁图 | 3 | 3 |
| 获批非错题裁图及内容版本 | 27 / 273,274 字节 | 0 |
| 获批路径的待回收记录 | 27 | 0 |
| 既有错题的标识与审核等受保护数据 | 71 条 | 71 条，指纹一致 |
| 既有练习 | 0 | 0 |

33 个受保护图像逐一 SHA256 不变。既有错题、练习及当前题目草稿的组合校验指纹为 `08078a3dd9903fa3c2756ab66675f3240933428df12c2b3614b4a1d7701f2dac`。当前题目与其版本标识保留；非当前历史快照无遗留几何，原任务的 `candidate_json` 已清空、`layout_json` 为 `[]`。没有把 S3 删除请求成功当作验收依据：回读核实了获批路径的所有内容版本均消失、保护图像字节一致、回收队列无获批路径。

操作工具：[ApprovedCropMigration](../../script/verification/ApprovedCropMigration/Program.cs)。它锁定清单哈希、任务版本、对象内容版本、引用与备份字节，仅参数化更新获批对象。故意损坏一个备份的负向检查被 `backup_bytes_mismatch` 拒绝。工具构建 0 warning / 0 error；相关回收与取消安全逻辑的实现验证见既有实现记录。

## 恢复材料

远程目录 `/home/zhf/docker/emenda/releases/20261122-corrections/` 保留受限权限的旧启动脚本、旧运行配置、`objects-final/` 的 60 个对象字节及 manifest、`before-schema20-final.dump`。最终数据库备份在旧实例停止后生成，`pg_restore --list` 读取通过，SHA256 为 `a9205881b62074cd437a91a6454095d0400c3bdc8d95c2892e8b454ebb84bdb7`。未执行恢复。

本机另有受限副本 `/Users/haifeng/.cache/emenda-approved-20261122-final`。这些恢复材料含业务数据，不进入 Git。schema v20 不支持简单 down migration；恢复需要匹配数据库与对象备份，不能只换回旧镜像。任何恢复也不得静默丢弃备份之后产生的验收记录。

AI Tutor 数据归档位于 `/home/zhf/docker/ai-tutor/releases/20261122-emenda-rc3/data-before.tgz`，SHA256 `d0f2c449b073fea0d97ef2e0774d7b841a62bfcba027798959f1d9b3e537b58a`。曾失败的复制目录标记为 `data-before.PARTIAL`，不能用作恢复材料。临时 GHCR 登录目录与损坏备份测试副本已删除，正式恢复材料保留。

## 首次实际 40 题分析与当时阻塞（后续已修复）

使用已有合成验收学生 `ce3a41f8-f809-4dc9-85bf-26ef0d793fd7`（Emenda UI 验收学生 52b010），通过真实登录的 `http://192.168.31.5:8001/` 门户选择两张 PNG 并点击提交；POST 返回 `202`。这是可复现的合成印刷题样本，通过真实部署的图像分析链路处理，并非模拟模型返回，也不代表自然拍照试卷质量已经验收。

新任务 `36e962f1-4feb-48d6-8254-6198e0fe870d` 已 `Succeeded`、版本 `1`。40 个 UUID 唯一，题号 1–40 完整，40 题的学生答案、参考答案、判对错及裁图数量逐题比较全部一致；10 道错题有 10 张裁图，30 道正确题没有裁图。第 33 题识别为错误，学生答案 65、参考答案 67，符合样本。

样本生成器、两页图片、预期与实际比对结果保留在 [acceptance-2026-11-22](acceptance-2026-11-22/)。本次没有先人为删除识别结果来宣称自然漏题，也未执行确认。

实际门户列表读取返回 `502 / EMENDA_INVALID_RESPONSE`。`Ruoyu.Study` 共享 `EmendaWireValidation.Questions` 在 `origin/master` 的源码 `806589f6291f4dd41a366e9e1dafae736feebea8` 中仍有整卷 32 题上限，并要求正确/未知题的裁图数量等于区域数量，与 extraction v2 当前裁图规则不兼容。Gitee PR328 顶层提交移除了数量限制，但仍需处理无裁图的正确/未知题；该 PR head `d0f263aa9fd4cc4ac04ead303348e22b66a6ff7c` 不是当时 master 的祖先。现有 Portal 镜像缺少可靠源码 revision 标签，未把镜像日期标签当作源码证明。

准备阶段曾生成以 `806589f...` 为基线的最小读取兼容预览；最终交付以 [已合入提交 947aa1c5](https://gitee.com/philfanzhou/Ruoyu.Study/commit/947aa1c5ff1fb319efd6183f42f77dd309e4a728) 为准，过时预览补丁已清理。该修复去掉整卷数量上限；允许正确/未知题无裁图；仍校验每个原图区域、所有实际裁图；错题不能缺裁图；原有每题 32 区域和 8 MiB 响应边界不变，旧生产者已有的非错题裁图仍按完整规则验证。

新增测试先取得 4 fail / 1 pass，修改后完整 client suite 为 **244 pass / 0 fail / 0 skip**，Release build **0 warning / 0 error**。补丁语法及反向应用检查通过；独立只读代码审查未发现可操作缺陷。它尚未提交、推送或部署，也没有冒充 PR328 的补题功能已经完成。现有 `Ruoyu.Study` 工作树的未提交变更未修改，预览在独立的源码归档中准备。

待用户批准的新增范围：核验整合 PR328 与上述兼容改动，经评审后合入 Gitee master，仅更新测试机的三端 BFF 和前端，然后完成门户 32→33 原图补题、稳定标识/审核保持及一次确认验收。此前仅批准的 Emenda 迁移不能自动扩展为 Portal 服务部署授权。

## Issue 状态

#18–#20 的服务端实现与专项回归已合并；#21 的获批测试迁移和回收验收已完成；#22 的真实分析、约定的当前版本门户交互及持久化回读已通过。历史版本查看不可用仍列为附加限制。没有声称五个 issue 已关闭：此前 GitHub issue 更新接口返回 `403 Resource not accessible by integration`，未绕过该拒绝；关闭操作仍需具备相应权限的维护者或恢复集成权限。
