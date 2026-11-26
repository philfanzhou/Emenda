# 统一错题入库模型设计

状态：阶段 A 已实现（schema v24，v0.1.1-rc.17；v25 修正已删任务的调用方反查，rc.18） · 阶段 B 已实现（契约 mistake-http-v7，v0.1.1-rc.19，无 schema 变更） · 阶段 C 待 Portal 切到 v7 后进行

## 1. 背景与目标

Emenda 现在有两条错题来源，都由 Ruoyu Portal 发起：

| | 上传提取（extraction） | 答疑采集（conversation） |
|---|---|---|
| 入口 | `POST /api/.../extractions[/images]` | `POST /api/mistakes/conversation-collections` |
| 题目内容 | Emenda 自己版面分析 + 调 AI Tutor `analyze-images` 识题 | Emenda 按 operationId 从 AI Tutor `question-operations/{id}` 拉取已识别好的题 |
| 生命周期 | 一次性批次：版本、更正提案、补充图片、逐题审核、发布 | 持续增量：多次作答、更正、撤回、回执防重放、清空历史 |
| 写入位置 | `ExtractionService.Revisions.cs` `PublishExtractionAsync` | `ConversationCollectionStore.ApplyAsync` |

两条管道的**前半段差异是业务本身决定的，保留**。问题出在后半段：它们写进 `mistake_item` 的格式不同，导致下游每个功能都要先判断“这是哪种错题”。

**目标**

1. 每道错题都有同一套“题目内容”字段（题干、学生作答、参考答案、解析、证据、知识点、图片），下游只读这一套。
2. 来源类型、调用方归属用显式字段表达，不再靠解析 `source_upload_id` 前缀或判断 `conversation_source IS NULL`。
3. 两条管道通过同一个写入组件（Materializer）落库。
4. 迁移不中断服务、不改变现有图片引用安全机制、不让答疑错题的 evidence hash 失效。

**非目标**

- 不把答疑采集改成上传任务（理由见上一轮分析：识题重复、生命周期和授权模型都不同）。
- 不在本设计里解决“题目配图”的存储（之前已暂缓），只在图片模型里预留角色。
- 不改审核流程本身（提取错题仍走 `ExtractionReviewEndpoints`，答疑错题仍走通用审核接口）。

## 2. 现状：`mistake_item` 字段按来源的含义

| 字段 | 上传提取 | 答疑采集 | 平台时代遗留 |
|---|---|---|---|
| `source_upload_id` | `extract:{jobId:N}` | `''` | 旧上传 ID |
| `question_id` | 提取题目 ID | **NULL**（题目 ID 只在 `conversation_source.questionId`） | 旧值 |
| `analysis_snapshot` | `{statement, studentAnswer, referenceAnswer, explanation, evidence, sourceVersion}`，任务删除后还会被塞入 `retainedFiles` 清单 | NULL | NULL |
| `conversation_source` | NULL | 完整 `ConversationQuestionSnapshot`（题干、作答、证据、参考答案、知识点、原文、图片、attempts、模型分析） | NULL |
| `conversation_caller` | NULL（调用方要从 `extraction_job.caller` 或 `extraction_deletion.owner_hash` 反查） | 调用方名 | NULL |
| `conversation_active` | false | 当前是否存在有效错误作答 | false |
| `source_regions` | 已裁好的题目 PNG（`mistakes/extractions/{job}/versions/...`） | **原图 + Tutor 裁图混在一起**，裁剪框只在 snapshot 里 | 旧图 + boundingBox |

由此产生的具体问题：

| # | 问题 | 位置 |
|---|---|---|
| P1 | 题目内容分散在两个 JSON 列，结构不同（作答一个是 `studentAnswer`，一个是 `studentAttempt.quote`；知识点只有答疑有；解析只有提取有） | `MistakeDtos.cs`、`MistakeHttpEndpoints.cs:70-76` |
| P2 | 来源靠字符串前缀判断，散落在 6 处以上 | `MistakeImageEndpoints.cs:40`、`RetainedPracticeImageGuard.cs:21`、`MistakeHttpEndpoints.cs:199`、`ExtractionService.Archive.cs`、`MistakeItemRepository.cs:77` |
| P3 | 调用方隔离的查询过滤器对提取错题要做两个子查询（任务表 + 删除表的 owner hash） | `MistakeDbContext.cs:59-65` |
| P4 | `analysis_snapshot` 同时承载“题目内容”和“归档文件清单 `retainedFiles`” | `ExtractionService.ArchiveManifest.cs:48` |
| P5 | 答疑错题的 `source_regions` 混合原图和裁图，`/images/{index}` 无法区分哪张是题目图 | `ConversationCollectionStore.cs:149` |
| P6 | 两条管道各自负责建行、图片变体入队；提取写 Created 处理日志，答疑不写 | `ExtractionService.Revisions.cs:134`、`ConversationCollectionStore.cs:107` |
| P7 | `mistake_analysis`、`reanalyze_job.analysis_input` 目前没有任何读写方 | `MistakeAnalysisSchema.cs` |

## 3. 设计原则

1. **扩展 → 回填 → 切读 → 收缩**，每一步单独可发布、可回滚。
2. **`source_regions` 语义和内容不变**：它是对象引用的唯一依据（引用触发器 `guard_extraction_object_reference`、清理时的引用检查、变体回填、预签名校验都依赖它），本设计不动它的结构，只规定它和新图片列表的对应关系。
3. **答疑快照逐字节保留**：`ConversationAnalysisStore` 的 evidence hash 由快照和 `source_regions` 计算，迁移不能改变这两者，否则 Tutor 侧写回的模型分析会全部被判为 `EVIDENCE_CHANGED`。
4. 来源特有的业务数据放在来源明细里，不强行抽象到通用字段。

## 4. 目标数据模型

### 4.1 `mistake_item` 列变化

| 列 | 动作 | 说明 |
|---|---|---|
| `source_kind text NOT NULL` | 新增 | `extraction` / `conversation` / `legacy`，CHECK 约束 |
| `source_caller text NULL` | 新增 | 统一的调用方归属。只有 `legacy` 和暂未解析的历史数据可为 NULL |
| `content jsonb NOT NULL` | 新增 | 统一题目内容，见 4.2。每行都有；来源没有提供的文字字段为 JSON null |
| `source_detail jsonb NULL` | 新增 | 来源明细，见 4.3 |
| `question_id` | 保留 | 答疑错题的题目 ID 在 `source_detail.questionId`；是否回填到本列留到阶段 B 决定（阶段 A 不改旧列） |
| `source_upload_id` | 保留 | 仍用于“按上传批次待审核”分组和 v6 契约；不再用于判断来源 |
| `conversation_active` | 保留 | 是答疑特有的生命周期状态，被列表过滤（`reviewStatus=4`）使用 |
| `source_regions` | 保留，不变 | 见 4.4 的对应规则 |
| `analysis_snapshot` | 阶段 C 删除 | 内容迁入 `content`，`retainedFiles` 迁入 `source_detail` |
| `conversation_source` | 阶段 C 删除 | 原样迁入 `source_detail` |
| `conversation_caller` | 阶段 C 删除 | 迁入 `source_caller` |

### 4.2 `content`（统一题目内容，version 1）

```json
{
  "version": 1,
  "statement": "题干",
  "studentAnswer": "学生作答",
  "referenceAnswer": "参考答案",
  "explanation": "解析",
  "evidence": "判错依据",
  "knowledgePoints": ["知识点"],
  "images": [
    { "path": "mistakes/...", "role": "question", "box": null }
  ]
}
```

字段来源映射：

| content 字段 | 上传提取 | 答疑采集 | legacy |
|---|---|---|---|
| statement | `analysis_snapshot.statement` | `conversation_source.statement` | — |
| studentAnswer | `analysis_snapshot.studentAnswer` | `conversation_source.studentAttempt.quote` | — |
| referenceAnswer | `analysis_snapshot.referenceAnswer` | `conversation_source.referenceAnswer` | — |
| explanation | `analysis_snapshot.explanation` | null | — |
| evidence | `analysis_snapshot.evidence` | `conversation_source.evidence` | — |
| knowledgePoints | `[]`（提取暂无） | `conversation_source.knowledgePoints` | — |
| images | 每个 crop → `role=question` | `assetId` 非空 → `question`，box 取快照里的裁剪框；原图 → `source` | `role=question` + 原 boundingBox |

表中 “—” 和来源缺失的字段写成 JSON null，`knowledgePoints` 缺失时为 `[]`。

`images[].role` 取值：`question`（题目图，展示用）、`source`（原图，仅作证据）。预留 `figure`，等配图议题定下来再启用。

### 4.3 `source_detail`（来源明细）

- `extraction`：`{ "jobId", "questionId", "sourceVersion", "retainedFiles": [...] }`。`retainedFiles` 只在任务删除后存在，结构不变（`Path, ContentType, Length, Sha256`）。
- `conversation`：**原样等于现在的 `conversation_source`**，包括 attempts、sources、images、analysis。不拆、不改字段，保证 evidence hash 不变。
- `legacy`：NULL。

### 4.4 图片：`content.images` 与 `source_regions` 的对应

不变式：**`content.images[i].path == source_regions[i].sourceImagePath`，两者长度相同、顺序相同**，由 Materializer 从同一个列表一次性写出。

- 这样 `/api/mistakes/{id}/images/{index}` 的下标含义不变，旧客户端不受影响。
- 新客户端读 `content.images[i].role` 就知道第 i 张是题目图还是原图，P5 得到解决。
- 引用安全机制继续只看 `source_regions`，不需要改。

### 4.5 约束

```sql
CONSTRAINT "CK_mistake_item_source_kind"   CHECK (source_kind IN ('extraction','conversation','legacy'))
CONSTRAINT "CK_mistake_item_source_detail" CHECK ((source_kind='legacy')=(source_detail IS NULL))
```

`source_kind` 和 `content` 为 NOT NULL。生产数据量很小（68 行），约束在回填后直接添加，不需要 `NOT VALID` 分步。

## 5. 统一写入：`MistakeMaterializer`

放在 Database 层，两条管道只负责产出候选，落库由它统一完成：

```csharp
public enum MistakeSourceKind { Extraction, Conversation, Legacy }
public enum MistakeImageRole { Question, Source }

public sealed record MistakeImage(string Path, MistakeImageRole Role, int[]? Box, int? Width, int? Height);
public sealed record MistakeContent(string Statement, string StudentAnswer, string ReferenceAnswer,
    string Explanation, string Evidence, string[] KnowledgePoints, MistakeImage[] Images);

public sealed record MistakeCandidate(
    Guid Id, MistakeSourceKind Kind, string Caller, string StudentId,
    string SourceUploadId, string QuestionId, int Subject, int Grade,
    MistakeContent Content, JsonNode SourceDetail, bool ConversationActive);
```

| 由 Materializer 负责 | 仍由各管道负责 |
|---|---|
| 新建/更新 `mistake_item` 行 | 提取：版本、更正提案、补充图片、归档清单 |
| 由 `Content.Images` 同时写出 `content.images` 和 `source_regions` | 答疑：attempt 合并、撤回、回执、老师审核后的保护判断 |
| 写 `source_kind / source_caller / source_detail` | 两边各自的授权和租约检查 |
| 写 Created / Updated 处理日志（补齐 P6） | 审核入口（保持现状） |
| 图片变体入队 | |

Materializer 在**阶段 B** 引入。阶段 A 不改两条管道的代码，新列由数据库触发器从旧列推导（见 7.1）。

**阶段 B 的实际实现**（与上面的草案不同，原因见 7.2）：

- `MistakeMaterializer`（`src/Database/MistakeMaterializer.cs`）挂在 `MistakeDbContext.SaveChanges[Async]` 上：
  每次 EF 保存新增或修改的 `mistake_item`，都由它从来源专属列推导出 `source_kind`、`source_caller`、`content`、`source_detail`。
  两条管道和通用仓储都经过它，不需要各自构造候选对象；被跟踪的实体在保存后就带着与数据库一致的新列值。
- 映射规则与触发器 `derive_mistake_item_source` 逐项相同（包括 JSON null 与缺失的区别、`jsonb_strip_nulls`、
  已删任务保留原调用方）。`MistakeMaterializerParityPostgresTests` 对同一批写入逐行比对两边的结果，任何一边改了映射都会失败。
- 触发器保留，继续覆盖原生 SQL 写入，并保证数据库里的值永远按这套规则得出。
- P6：答疑错题首次入库时也写一条 Created 处理日志（`operation=conversation_collected`）。
- 图片变体入队仍留在两条管道里：两边入队的时机（提取只入队新增裁图、答疑在上传后入队）各有理由，搬进 Materializer 没有收益。

## 6. 读取侧改动

| 位置 | 现在 | 改后 |
|---|---|---|
| 调用方查询过滤器 `MistakeDbContext.cs:59` | conversation_caller + 两个子查询 | `source_caller = scope`；`source_caller IS NULL` 的历史行保留旧子查询兜底，直到解析完成 |
| `MistakeImageEndpoints` | 解析 `extract:` 前缀 | `switch (source_kind)` |
| `RetainedPracticeImageGuard` / 归档读取 | 读 `analysis_snapshot.retainedFiles` | 读 `source_detail.retainedFiles` |
| 通用审核接口 | 前缀 + `conversation_source != null` | `source_kind` |
| 待审核分组 | `conversation_source == null` | `source_kind <> 'conversation'` |
| DTO / 契约 | v6：`analysisSnapshot`、`conversationSource` | **v7**：新增 `sourceKind`、`content`；v6 两个字段继续输出（由新列派生），直到 Portal 切完 |

阶段 B 已全部切换（rc.19）。另外答疑模型分析写回的归属校验和 evidence 快照（`ConversationAnalysisStore`）也改为读新列；
`source_detail` 与 `conversation_source` 逐字节相同，evidence hash 不受影响。答疑管道合并时读取的上一版快照（`ConversationCollectionStore`）
仍读 `conversation_source`：同一批次里前一个事件可能已在内存中改了它，而 `source_detail` 要到保存时才重新推导。
调用方过滤器的新旧等价性由 `CallerScopeFilterPostgresTests` 按全部归属情形（遗留、各调用方任务、管理员任务、已删任务已解析/未解析/他人、
无效任务号、各调用方答疑、无调用方答疑）逐个调用方比对。

两处与表格不同的取舍：
- v6 的 `analysisSnapshot`、`conversationSource` 仍从旧列读取（旧列在阶段 B 照常写入，内容相同），阶段 C 删旧列时再改。
- `content` 的文字可见范围沿用 v6：提取错题只在待审核或已确认时给出文字（同 `analysisSnapshot`），答疑错题同 `conversationSource`；
  图片列表始终给出。列表接口也返回 `content`。

## 7. 数据迁移

### 7.0 迁移前在生产库执行的只读盘点

**2026-11-26 在 55.10（与生产数据一致）上的结果：** `mistake_item` 共 68 行，全部是 `legacy`：同一个学生、全部已确认（review_status=2）、
每行 1–4 张整页上传图、没有 boundingBox、没有题目文字。提取任务、删除回执、答疑任务和答疑错题均为 0 行。
因此 `legacy` 类型必须保留；evidence hash 和已删任务调用方反查在生产上没有存量数据，只需保证通用逻辑正确。

```sql
-- 各来源、各审核状态的行数，以及缺内容的行数
SELECT CASE WHEN conversation_source IS NOT NULL THEN 'conversation'
            WHEN source_upload_id LIKE 'extract:%' THEN 'extraction'
            ELSE 'legacy' END AS kind,
       review_status,
       count(*) AS total,
       count(*) FILTER (WHERE conversation_source IS NULL AND analysis_snapshot IS NULL) AS without_content
FROM mistake_item GROUP BY 1, 2 ORDER BY 1, 2;

-- 任务已删除、需要通过 owner_hash 反查调用方的提取错题
SELECT count(*) FROM mistake_item m
WHERE m.source_upload_id LIKE 'extract:%'
  AND NOT EXISTS (SELECT 1 FROM extraction_job j
                  WHERE m.source_upload_id = 'extract:' || replace(j.id::text, '-', ''));
```

这一步的结果决定两件事：`legacy` 行是否存在（不存在就可以不保留这个类型），以及有多少行需要靠 owner_hash 解析调用方。

### 7.1 阶段 A：扩展 + 回填 + 触发器推导（v0.1.1-rc.17，已实现）

迁移 `20261126000000_UnifiedMistakeSource`（schema v24，`src/Database/UnifiedMistakeSchema.cs`）：

1. 加列 `source_kind`、`source_caller`、`content`、`source_detail`。
2. 建函数 `derive_mistake_item_source()` 和 `BEFORE INSERT OR UPDATE` 触发器 `trg_mistake_item_source`：
   每次写 `mistake_item` 时按 4.2/4.3 的映射从旧列推导新列。**旧列仍是权威数据**，直接写新列的值会被覆盖。
   - 选触发器而不是在两条管道里双写：所有写入方（EF、原生 SQL、迁移脚本）自动一致，两条管道的代码零改动，
     映射规则只有一份，回填和日常写入用的是同一段 SQL。
   - 提取错题的调用方取自 `extraction_job.caller`；任务被删后查不到时保留之前的值。
3. 回填：`UPDATE mistake_item SET source_upload_id=source_upload_id` 让每行经过一次触发器，`updated_at` 不变，
   不影响乐观并发。
4. 已删任务的提取错题：用库里已知的调用方名反查 `extraction_deletion.owner_hash`。回执存的是应用对 JSON 字符串
   `"caller:<name>"`（含引号）求的 sha256；v24 误用了不带引号的原文，一条也匹配不上，由 v25 迁移
   （`ExtractionCallerRecovery`）按正确格式重做。名称含非 ASCII 或 `" & ' + < > \` 反引号等会被 JSON 转义的字符时不参与比较。
   仍解析不了的保持 NULL；阶段 A 的读取仍走旧的子查询，可见性不变。
5. `source_kind`、`content` 设为 NOT NULL；加 CHECK 约束 `CK_mistake_item_source_kind`、`CK_mistake_item_source_detail`
   （`legacy` 当且仅当 `source_detail IS NULL`）。
6. 迁移内校验：任一行 `content.images` 与 `source_regions` 长度或逐项路径不一致、或答疑行 `source_detail` 与
   `conversation_source` 不相等，就抛 `unified_mistake_backfill_mismatch`，整个升级回滚。
7. 严格 schema 校验器登记 v24 的列、约束、函数和触发器。

应用代码在阶段 A **不读写**新列，实体也不映射它们（映射后，现有在旧版本 schema 上插数据的迁移测试会失败）。

**回滚**：与 v20、v22、v23 一样是只进不退的迁移（Down 抛 `unified_mistake_source_irreversible`）。旧版本镜像的严格校验器
不认识 v24，不能直接回退镜像；如需回退，手工删除触发器、函数、四列和 `__EFMigrationsHistory` 中的这一行即可，
新列没有独立数据，不会丢信息。

### 7.2 阶段 B：Materializer + 切换读取（下一个版本）

- 引入 `MistakeMaterializer`，两条管道改为通过它写入，新列成为权威数据；触发器改为只校验或移除。
- 实体映射新列。
- 第 6 节列出的读取点全部改为读新列。
- 发布契约 v7：新增 `sourceKind`、`content`，v6 字段由新列派生继续输出。
- 旧列继续写，确保回退到阶段 A 的代码时数据一致。

**实际实现（v0.1.1-rc.19）**：上面第一条按“回滚不涉及 schema”的要求调整为**保留触发器**。改触发器（只校验或删除）
本身就是 schema 变更：新版本号会让 rc.18 的严格校验器拒绝启动，回退就不再是“换回镜像”这么简单。因此阶段 B：

- 数据库仍由触发器 `trg_mistake_item_source` 推导新列；应用侧的 `MistakeMaterializer` 在每次 EF 保存时用同一套规则写出新列
  （见第 5 节），两边由一致性测试锁定。数据库里的新列因此始终由旧列唯一决定，阶段 B 和阶段 A 的代码看到的数据完全一样。
- 实体映射新列（`SourceKind`、`SourceCaller`、`Content`、`SourceDetail`），第 6 节的读取点全部改读新列，契约升到 v7。
- 没有新迁移，schema 仍是 v25。
- “新列成为权威数据、触发器改为只校验或移除”挪到阶段 C：届时旧列被删除，触发器的推导来源也随之消失，必须一起处理。

**回滚**：直接换回 rc.18 镜像即可，schema 不变，数据不需要任何处理。

### 7.3 阶段 C：收缩（Portal 切到 v7 且稳定运行一个版本后）

- 停止写旧列；迁移删除 `analysis_snapshot`、`conversation_source`、`conversation_caller`。
- Down 先检查：若存在只在新列里有、旧列无法还原的数据则拒绝降级（与现有迁移的做法一致）。
- 契约 v8 去掉 `analysisSnapshot`、`conversationSource`（或在 v7 中标记废弃，按 Portal 节奏决定）。
- 同时评估清理 P7 中的闲置表 `mistake_analysis`、`reanalyze_job.analysis_input`。

## 8. 发布顺序

| 步骤 | Emenda | Ruoyu Portal |
|---|---|---|
| 1 | 阶段 A（rc.17）：加列、回填、触发器推导 | 无需改动 |
| 2 | 阶段 B（rc.19）：Materializer、切读，发布契约 v7 | 无需改动（v6 字段仍在） |
| 3 | — | 改为读 `content` / `sourceKind`，图片按 `content.images[i].role` 区分题目图和原图 |
| 4 | 阶段 C：删旧列，契约 v8 | 确认不再读 v6 字段 |

## 9. 风险

| 风险 | 应对 |
|---|---|
| evidence hash 变化导致模型分析写回全部失败 | `source_detail` 原样复制、`source_regions` 不动；迁移校验逐行比对 |
| 已删除任务的错题解析不出调用方，被调用方过滤器隐藏 | 阶段 A 读取不变；阶段 B 切读时对 NULL 保留旧子查询兜底，或用配置里的调用方名再解析一次。生产当前为 0 行 |
| 回填锁表时间 | 生产只有 68 行，单条 UPDATE 在毫秒级完成；数据量大时再改为分批 |
| 早期提取错题 `analysis_snapshot` 为空（rc.12 之前），`content` 文字字段为 null | 生产当前为 0 行；如需要，可复用 `WorksheetAnalysisSchema.Backfill` 的思路从 `extraction_revision` 再补一次 |
| 并发：迁移期间有新错题写入 | 阶段 A 的代码在迁移完成后才启动（现有启动流程先迁移再提供服务），不存在新旧代码并存写入 |

## 10. 待确认事项

1. Portal 接入 v7 的排期（决定阶段 C 什么时候做）。
2. ~~生产库里是否还有 `legacy` 行~~：有 68 行（见 7.0），`legacy` 类型保留。
3. 答疑错题的原图（`role=source`）是否要在错题详情里展示给学生/老师，还是只作为证据保留。
4. 题目配图是否沿用 `content.images` 加 `role=figure`，等配图议题重新讨论时确定。

## 11. 工作拆分

| PR | 内容 | 状态 |
|---|---|---|
| 1 | 阶段 A：v24 迁移、触发器推导、回填、迁移内校验、严格校验器登记 | 已完成（rc.17） |
| 1a | v25：按回执的真实哈希格式补回已删任务的调用方（test2 发现 2 条） | 已完成（rc.18） |
| 2 | 阶段 B：`MistakeMaterializer`、实体映射新列、读取侧切换、契约 v7 | 已完成（rc.19） |
| 3 | Ruoyu Portal 适配 v7 | 待做 |
| 4 | 阶段 C：删除旧列、契约 v8、闲置表清理 | Portal 稳定后 |
