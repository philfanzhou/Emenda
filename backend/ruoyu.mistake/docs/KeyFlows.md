# ruoyu.mistake 跨服务关键流程文档

本文档描述 ruoyu.mistake 服务与外部服务（student、teacher_portal、admin_portal）之间的关键交互流程。

---

## 流程1: student → mistake 的错题创建链路

### 基本信息

| 项目 | 说明 |
|------|------|
| **触发方** | ruoyu.student |
| **前置条件** | 学生上传图片完成，VL 分析服务已配置（可选） |
| **调用接口** | `SubmitMistakeUpload` (gRPC) |
| **副作用** | 在 mistake 中创建 MistakeItem 记录（ReviewStatus=PendingReview），写入 ProcessLog(Created) |
| **失败处理** | 由调用方（student）保证每个上传记录只处理一次；mistake 侧无额外重试 |

### 流程图

```
┌─────────────────────────────── student 服务 ───────────────────────────────┐
│                                                                            │
│  UploadAnalysisWorker                GrpcUploadDispatcher                  │
│       │                                      │                             │
│       ▼                                      ▼                             │
│  从队列获取上传记录                    持有 MistakeGrpcServiceClient        │
│       │                                      │                             │
│       ▼                                      │                             │
│  VL 已配置? ──否──┐                          │                             │
│       │          │                          │                             │
│      是          │                          │                             │
│       ▼          ▼                          ▼                             │
│  AnalyzeAsync   跳过分析           SubmitMistakeUpload (gRPC)             │
│       │                                  ──────────────────┐              │
│       ▼                                                    │              │
│  HasGroups? ──否──┐                                        │              │
│       │          │                                        │              │
│      是          │                                        │              │
│       ▼          │                                        │              │
│  遍历 group:     │                                        │              │
│   提取图片路径    │                                        │              │
│   IMistakeSubmitter                                       │              │
│   .SubmitMistakeUploadAsync                               │              │
│       │                                                   │              │
│       ▼                                                   │              │
│  标记上传记录 Completed                                    │              │
│                                                            │              │
└────────────────────────────────────────────────────────────┼──────────────┘
                                                             │
                                                             ▼
┌──────────────────────────── mistake 服务 ──────────────────────────────────┐
│                                                                            │
│  SubmitMistakeUpload                                                       │
│       │                                                                    │
│       ▼                                                                    │
│  参数校验                                                                   │
│       │                                                                    │
│       ▼                                                                    │
│  创建 MistakeItem (ReviewStatus=PendingReview)                             │
│       │                                                                    │
│       ▼                                                                    │
│  记录 ProcessLog (Created)                                                 │
│       │                                                                    │
│       ▼                                                                    │
│  SaveChanges                                                               │
│       │                                                                    │
│       ▼                                                                    │
│  返回 created_item_ids                                                     │
│                                                                            │
└────────────────────────────────────────────────────────────────────────────┘
```

### 两条触发路径

**路径 A: UploadAnalysisWorker**

1. 从队列获取上传记录
2. 如果 VL 已配置，调用 `analyzer.AnalyzeAsync` 分析图片
3. 如果分析结果有分组（HasGroups），遍历每个 group：
   - 从 `group.ImageIndices` 提取对应的图片路径
   - 调用 `IMistakeSubmitter.SubmitMistakeUploadAsync`
   - 传入: `studentId`, `subject`, `grade`, `selectedImagePaths`, `comments`, `record.Id.ToString()`
4. 标记上传记录为 Completed

**路径 B: GrpcUploadDispatcher**

- 持有 `MistakeGrpcServiceClient`，可直接调用 `SubmitMistakeUpload` 分发到错题服务
- 同时也支持 `DispatchToHomeworkAsync` 分发到作业服务

### 幂等性与 source_upload_id 关系

| 项目 | 说明 |
|------|------|
| `SubmitMistakeUpload` 是否幂等 | **否**，每次调用创建新条目 |
| `source_upload_id` 唯一性校验 | **不做**，同一 `source_upload_id` 可以多次提交 |
| 幂等性保证方 | 由调用方（student）保证：每个上传记录只处理一次 |

---

## 流程2: 审核完成链路

### 基本信息

| 项目 | 说明 |
|------|------|
| **触发方** | teacher_portal |
| **前置条件** | 存在 PendingReview 状态的 MistakeItem |
| **调用接口** | `GetPendingReviewUploads` → `GetMistakeItemsByUpload` → `ReviewMistakeItem` → `CompleteUploadReview` |
| **副作用** | OSS 图片迁移（uploads/ → mistakes/）、student 上传记录图片移除、portal 层额外调用 DeleteUploadRecordAfterReviewAsync |
| **失败处理** | OSS 单图迁移失败不阻塞；student 回调失败仅 log error 不回滚；审核完成是业务关键节点，不因临时不可用而回滚 |

### 流程图

```
┌─────────────────────── teacher_portal ──────────────────────┐
│                                                              │
│  1. GetPendingReviewUploads ──→ 获取待审核列表               │
│                         │                                    │
│                         ▼                                    │
│  2. GetMistakeItemsByUpload ──→ 查看审核详情                 │
│                         │                                    │
│                         ▼                                    │
│  3. 逐条 ReviewMistakeItem ──→ Confirmed / Rejected         │
│       │                                                      │
│       │  驳回时额外操作 (portal 层):                         │
│       │    DeleteMistakeItem + student.MarkUploadRecordAsReturned
│                         │                                    │
│                         ▼                                    │
│  4. CompleteUploadReview ──→ 完成整批审核                    │
│                         │                                    │
│                         ▼                                    │
│  5. student.DeleteUploadRecordAfterReviewAsync (portal 层)   │
│                                                              │
└──────────────────────────────────────────────────────────────┘
                         │
                         │  步骤 3 & 4 涉及 mistake 内部
                         ▼
┌─────────────────────── mistake 内部 ─────────────────────────┐
│                                                               │
│  ReviewMistakeItem:                                           │
│    ├─ 校验状态必须为 PendingReview                             │
│    ├─ 可修正 grade(1-12) 和 subject(1-9)                      │
│    ├─ 记录 ProcessLog (ReviewConfirmed / ReviewRejected)      │
│    └─ 更新 MistakeItem 状态                                   │
│                                                               │
│  CompleteUploadReview:                                        │
│    ├─ DomainService 校验: source_upload_id 下无 PendingReview │
│    ├─ 返回所有 item 列表和 studentId                          │
│    │                                                          │
│    ├─ 遍历 items 的 source_regions:                           │
│    │   ├─ Confirmed item:                                     │
│    │   │   OSS copy uploads/{path} → mistakes/{path}          │
│    │   │   OSS delete uploads/{path}                          │
│    │   │   更新 source_regions 路径                           │
│    │   ├─ Rejected item: 不迁移图片                           │
│    │   └─ 单图迁移失败: catch → log error → 继续              │
│    │                                                          │
│    ├─ pathMapping 缓存: 避免重复迁移同一图片                  │
│    │                                                          │
│    ├─ 跨服务调用 student.RemoveImagesFromRecord:              │
│    │   ├─ 移除 Confirmed + Rejected 的所有图片                │
│    │   └─ 失败时: 仅 log error，不回滚                       │
│    │                                                          │
│    └─ 返回 success=true + studentId                          │
│                                                               │
└───────────────────────────────────────────────────────────────┘
```

### ReviewMistakeItem 流程

| 步骤 | 说明 |
|------|------|
| 状态校验 | 只能审核 PendingReview 状态的错题 |
| 修正字段 | 可在审核时修正 grade(1-12) 和 subject(1-9) |
| 日志记录 | 记录 ProcessLog（ReviewConfirmed / ReviewRejected） |
| 驳回额外操作 | **portal 层行为**: 调用 `DeleteMistakeItem` + `student.MarkUploadRecordAsReturned` |

### CompleteUploadReview 流程

**mistake 内部行为**（参考 MistakeServiceImpl.cs L261-365）：

1. `MistakeDomainService.CompleteUploadReviewAsync`:
   - 校验 `source_upload_id` 下不存在 PendingReview 的 item
   - 返回所有 item 列表和 studentId
2. `MistakeServiceImpl.CompleteUploadReview`:
   - 遍历所有 items 的 `source_regions`：
     - Confirmed item: OSS copy `uploads/{path}` → `mistakes/{path}`，OSS delete `uploads/{path}`，更新 `source_regions` 路径
     - Rejected item: 不迁移图片
     - 单个图片迁移失败不阻塞整体流程（catch 后继续）
   - 路径映射缓存 `pathMapping`: 避免重复迁移同一图片
   - 跨服务调用 `student.RemoveImagesFromRecord(recordId, studentId, imagePaths)`:
     - 移除 Confirmed 和 Rejected 的所有图片
     - 失败时仅 log error，不回滚整个审核完成流程
   - 返回 `success=true` + `studentId`

**portal 层额外行为**（非 mistake 内部）：

- `CompleteUploadReview` 成功后，teacher_portal 额外调用 `student.DeleteUploadRecordAfterReviewAsync`
- 这是 portal 层的额外清理逻辑，不是 mistake 内部行为

### 失败处理

| 失败场景 | 处理方式 | 设计理由 |
|----------|----------|----------|
| OSS 单图迁移失败 | 不阻塞，log error 后继续 | 审核完成是业务关键节点，不应因 OSS 临时不可用而回滚 |
| student 回调失败 | 仅 log error，不回滚审核完成状态 | 同上，不应因 student 临时不可用而回滚 |
| 整体原则 | 审核完成不回滚 | 审核完成是业务关键节点，不应因外部依赖临时不可用而回滚 |

---

## 流程3: 图片引用查询与 OSS 审计支持链路

### 基本信息

| 项目 | 说明 |
|------|------|
| **触发方** | admin_portal（OssAuditWorker） |
| **前置条件** | 无 |
| **调用接口** | `GetAllReferencedImagePaths` (gRPC) |
| **副作用** | 无（只读查询） |
| **失败处理** | mistake 服务不可用时，审计结果可能有假阳性（正常对象被误判为僵尸） |

### 流程图

```
┌──────────────── admin_portal (OssAuditWorker) ───────────────┐
│                                                                │
│  1. 调用 student.GetRegisteredOssPaths                         │
│     → 获取 uploads/ 下的注册路径                               │
│                                                                │
│  2. 调用 mistake.GetAllReferencedImagePaths                    │
│     → 获取 mistakes/ 下的引用路径                              │
│                                                                │
│  3. 列出 OSS 中所有对象                                        │
│                                                                │
│  4. 判定僵尸对象:                                              │
│     不在 registeredPaths 且不在 mistakePaths 中的对象           │
│                                                                │
│  ⚠️ mistake 服务不可用时:                                      │
│     mistakePaths 为空 → 正常 mistakes/ 对象被误判为僵尸         │
│                                                                │
└────────────────────────────────────────────────────────────────┘
                         │
                         ▼
┌─────────────────────── mistake 内部 ─────────────────────────┐
│                                                               │
│  GetAllReferencedImagePaths:                                  │
│    ├─ 收集 mistake_item.source_regions 中的路径               │
│    ├─ 收集 mistake_practice.image_paths 中的路径              │
│    └─ 去重后返回                                              │
│                                                               │
└───────────────────────────────────────────────────────────────┘
```

### GetAllReferencedImagePaths

| 项目 | 说明 |
|------|------|
| 参数 | 无 |
| 返回 | 系统中所有被引用的图片 OSS 路径（去重） |
| 数据来源 | `mistake_item.source_regions` + `mistake_practice.image_paths` |

### 调用方: admin_portal 的 OssAuditWorker

1. 获取 student 服务的 `GetRegisteredOssPaths`（uploads/ 下的注册路径）
2. 获取 mistake 服务的 `GetAllReferencedImagePaths`（mistakes/ 下的引用路径）
3. 列出 OSS 中所有对象
4. 不在 `registeredPaths` 且不在 `mistakePaths` 中的对象 = 僵尸对象
5. mistake 服务不可用时: 审计结果可能有假阳性（正常对象被误判为僵尸）

### 意义

- 支持 OSS 垃圾清理
- 支持 admin 审计
- 确保 `mistakes/` 下的图片不被误删

---

## 流程4: 错题练习与过程日志链路

### 基本信息

| 项目 | 说明 |
|------|------|
| **触发方** | 前端 / 外部服务 |
| **前置条件** | 错题必须存在且为 Confirmed 状态（练习场景） |
| **调用接口** | `AddMistakePractice`, `GetMistakePractices`, `LogMistakeProcess`, `GetMistakeProcessHistory`, `AnalyzeMistakeType` |
| **副作用** | 创建练习记录、写入 ProcessLog |
| **失败处理** | 前置条件不满足时返回错误 |

### 流程图

```
┌──────────────────── 错题练习 ────────────────────┐
│                                                    │
│  AddMistakePractice                                │
│    ├─ 前提: 错题必须是 Confirmed 状态              │
│    ├─ 创建练习记录，可选传入 imagePaths            │
│    └─ 记录 ProcessLog(Practiced)                   │
│                                                    │
│  GetMistakePractices                               │
│    └─ 查询指定错题的所有练习记录                   │
│                                                    │
└────────────────────────────────────────────────────┘

┌──────────────────── 过程日志 ────────────────────┐
│                                                    │
│  LogMistakeProcess                                 │
│    ├─ 前提: 错题必须存在                           │
│    ├─ 手动记录处理日志                             │
│    └─ action 类型:                                 │
│         Created / Analyzed / ReviewConfirmed /     │
│         ReviewRejected / Practiced / Mastered      │
│                                                    │
│  GetMistakeProcessHistory                          │
│    └─ 查询指定错题的所有处理日志，按时间正序       │
│                                                    │
└────────────────────────────────────────────────────┘

┌──────────────────── 错题分析 ────────────────────┐
│                                                    │
│  AnalyzeMistakeType                                │
│    ├─ 当前实现: 硬编码返回 type=5 "其他错误"      │
│    ├─ 记录 ProcessLog(Analyzed)                    │
│    └─ 未来可接入 VL 分析服务                       │
│                                                    │
└────────────────────────────────────────────────────┘
```

### 接口明细

| 接口 | 前提条件 | 行为 | ProcessLog Action |
|------|----------|------|-------------------|
| `AddMistakePractice` | 错题为 Confirmed 状态 | 创建练习记录，可选传入 imagePaths | Practiced |
| `GetMistakePractices` | 无 | 查询指定错题的所有练习记录 | — |
| `LogMistakeProcess` | 错题必须存在 | 手动记录处理日志 | Created / Analyzed / ReviewConfirmed / ReviewRejected / Practiced / Mastered |
| `GetMistakeProcessHistory` | 无 | 查询指定错题的所有处理日志，按时间正序 | — |
| `AnalyzeMistakeType` | 无 | 硬编码返回 type=5 "其他错误"，记录日志 | Analyzed |

### ProcessLog Action 类型一览

| Action | 触发场景 |
|--------|----------|
| Created | SubmitMistakeUpload 创建错题时 |
| Analyzed | AnalyzeMistakeType 调用时 |
| ReviewConfirmed | ReviewMistakeItem 确认时 |
| ReviewRejected | ReviewMistakeItem 驳回时 |
| Practiced | AddMistakePractice 练习时 |
| Mastered | LogMistakeProcess 手动标记时 |

---

## 跨服务调用关系总览

```
                    ┌─────────────┐
                    │ admin_portal │
                    │             │
                    │ OssAudit    │
                    │ Worker      │
                    └──────┬──────┘
                           │ GetAllReferencedImagePaths
                           ▼
┌─────────────┐    ┌───────────────┐    ┌────────────────┐
│   student   │───→│   mistake     │←───│ teacher_portal │
│             │    │               │    │                │
│ Upload      │    │ Submit        │    │ GetPending     │
│ Analysis    │    │ MistakeUpload │    │ ReviewUploads  │
│ Worker      │    │               │    │                │
│             │    │ Review        │    │ ReviewMistake  │
│ GrpcUpload  │    │ MistakeItem   │    │ Item           │
│ Dispatcher  │    │               │    │                │
│             │    │ Complete      │    │ CompleteUpload │
│             │←───│ UploadReview  │───→│ Review         │
│             │    │ (回调         │    │                │
│             │    │  RemoveImages)│    │ DeleteUpload   │
│             │    │               │    │ RecordAfter    │
│             │    │ GetAll        │    │ Review (portal)│
│             │    │ Referenced    │    │                │
│             │    │ ImagePaths    │    │                │
└─────────────┘    └───────────────┘    └────────────────┘
     ▲                   │
     │                   │
     └─── RemoveImagesFromRecord ───┘
          (CompleteUploadReview 内部回调)
```

### 调用方向说明

| 调用方 | 被调用方 | 接口 | 场景 |
|--------|----------|------|------|
| student | mistake | `SubmitMistakeUpload` | 错题创建 |
| mistake | student | `RemoveImagesFromRecord` | 审核完成后移除图片 |
| teacher_portal | mistake | `GetPendingReviewUploads` | 查看待审核列表 |
| teacher_portal | mistake | `GetMistakeItemsByUpload` | 查看审核详情 |
| teacher_portal | mistake | `ReviewMistakeItem` | 逐条审核 |
| teacher_portal | mistake | `CompleteUploadReview` | 完成整批审核 |
| teacher_portal | mistake | `DeleteMistakeItem` | 驳回时删除错题（portal 层行为） |
| teacher_portal | student | `MarkUploadRecordAsReturned` | 驳回时标记退回（portal 层行为） |
| teacher_portal | student | `DeleteUploadRecordAfterReviewAsync` | 审核完成后删除上传记录（portal 层行为） |
| admin_portal | mistake | `GetAllReferencedImagePaths` | OSS 审计 |
