# 遗留异常只读对账报告

`GET /api/mistakes/reconciliation`（可选 `?sourceUploadId=` 范围过滤，始终认证且 AdminOnly，接线沿 managed intake GET）基于引用全集与持久状态输出跨资源异常分类与修复预览，不执行任何修复。实现为 `SourceReconciliationService`（IKJT7M）；父项背景见 [#IKJ0IO](https://gitee.com/philfanzhou/Ruoyu.Study/issues/IKJ0IO)。

## 计算模型

- 引用全集复用 `Ruoyu.Study.Common.StorageReferences.StorageReferenceSource` 的 mistake 查询列与 `Parse` 语义（含 Rejected 墓碑 `source_return.original_item/payload`、`source_handoff.targets` 等），逐行记录表/行/字段来源；解析失败按其规则失败关闭，不静默跳过。
- 全部数据库状态在单一 PostgreSQL `REPEATABLE READ READ ONLY` 事务内读取（与 StorageReferences capture 同隔离级别），事务只回滚不提交；报告不持有任何行锁或 advisory lock。
- OSS 侧对每个引用键执行实际 `DownloadAsync` 核验（与 SourceTargets 选定验证同标准）：对象缺失（not-found 族异常）→ 悬挂 `missing`；有持久期望（manifest/target object/verified attempt/return object/handoff targets）且长度或 SHA256 不符 → 悬挂 `bytes_conflict`；其他读取异常 → 依赖不可用 503，不输出部分结果。
- 请求取消全程传播；报告运行前后业务表与 OSS 字节零变化。

## 异常类 × 唯一结果

| 类别 | 判定 | 结果 |
|---|---|---|
| danglingReferences | 引用键实际读取缺失或与持久期望冲突 | 报告键、原因、期望值与引用记录（表/行/字段，每条目上限 25 并带 `referencesTruncated`） |
| permanentConflicts | `source_handoff` 或 `source_return` 处于永久 Conflict（身份/载荷/回执不匹配） | 报告 kind/operationId/来源与冲突事实；不重试、不解封 |
| pendingRecovery | Prepared/Sending handoff 或 return、Reserved/Unknown/Copied target attempt、未 materialize 的 intake | 仅报告状态、kind 与可得的持久时间/年龄（attempt 与 intake 无持久时钟字段时为 null）；既有 worker/管理入口负责推进 |
| unmanagedReferences | `mistake_item`/`mistake_practice` 记录或键无法归属到 intake group / manifest / 选定 Verified 键（kind：`item_without_intake`、`item_outside_managed_group`、`region_unattributable`、`practice_without_managed_item`） | 分类=未知；托管 practice 的自有练习图沿 practice→item→group 链归属，不按前缀误报 |
| repairPreview | 汇总 unmanaged 差异清单 | 仅列 `classification=unknown`、计数与所需输入（可信旧输入或 owner 批准），不提议删除/搬移/补写 |

每类条目上限 100 并带 `truncated` 标记；`count` 始终是全量计数。Reserved/Unknown attempt 的目标键尚未写入对象，按定义同时计入悬挂引用，这是预期而非误报。

## 范围与排除

范围过滤 `?sourceUploadId=` 在同一只读事务内生效（按 `intake_source_key` 归一匹配 mistake_item）；过滤目标在本域完全未知时 404 `source_not_found`，非 GUID 或空 GUID 400 `invalid_condition`。匿名 401、非 Admin 403、依赖/持久数据失败 503 `reconciliation_unavailable`，错误 JSON 形状与 managed 路由一致（已纳入 `mistake-http-v1.json` 合同）。

本能力不做 OSS 列举与"未引用观察"（归外部 Admin Storage Audit）、不覆盖 Student 侧跨库异常、不做物理 GC/SourceReleased、不修改 StorageReferences v1 合同、不新增表/迁移/配置开关。

## 验证策略

`SourceReconciliationPostgresTests`（tests/integration provider 套件）覆盖：健康完整链路计数为零与只读证明（表计数+OSS 字节+仅下载）、缺失/篡改对象与 OSS 故障 503、真实 Conflict handoff 零推进、各非终态仅报告状态、遗留未托管分类与托管链路不误报、认证矩阵/过滤/404/400、条目截断计数准确、坏持久行 503、取消传播与并发报告不阻塞业务写。环境变量沿 [SourceIntake 验证](./SourceIntake.md)（`MISTAKE_INTAKE_TEST_CONNECTION`/`STUDENT_SCAN_TEST_CONNECTION`，任务自有 PG15）。

```bash
dotnet build tests/integration/Ruoyu.Study.IntegrationTests.sln --configuration Release
dotnet test src/services/ruoyu.mistake/src/Ruoyu.Study.Mistake.sln --configuration Release
dotnet test tests/integration/src/Ruoyu.Study.Mistake.Provider.Tests/Ruoyu.Study.Mistake.Provider.Tests.csproj --configuration Release --filter FullyQualifiedName~SourceReconciliation
```
