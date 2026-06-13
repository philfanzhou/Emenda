# 三要素生命周期分析：UploadRecord / MistakeItem / OSS 对象

本文档分析上传记录、错题记录、OSS 对象三个核心要素在学生端、教师端、管理端中的所有操作，梳理当前服务职责混淆和逻辑游离的问题，并提出修改方向。

## 1. 架构概览

三个核心实体跨两个微服务，通过 gRPC 交互：

```
ruoyu.student 服务                    ruoyu.mistake 服务
┌─────────────────────┐              ┌──────────────────────┐
│ ImageUploadRecord   │──SourceUploadId──▶ MistakeItem       │
│  - Id               │   (字符串引用,   │  - SourceRegions    │
│  - ImagePaths       │    无外键约束)   │    - SourceImagePath│
│  - Status           │              │  - ReviewStatus      │
└─────────────────────┘              │  - ReviewerId        │
                                     └──────────────────────┘
OSS: uploads/xxx  ──审核确认后迁移──▶  OSS: mistakes/xxx
```

OSS 路径归属（参见 [DataOwnership.md](./DataOwnership.md)）：

| 路径前缀 | 主责服务 | 说明 |
|----------|----------|------|
| `uploads/` | ruoyu.student | student 服务主责，mistake 只引用 |
| `mistakes/` | ruoyu.mistake | mistake 服务主责，审核完成后图片迁移到此 |

## 2. 全景操作矩阵

### 2.1 对 UploadRecord 的操作

| 操作 | 学生端 | 教师端 | 管理端 | OSS 影响 |
|------|--------|--------|--------|---------|
| 创建（上传图片） | `UploadImages` / `UploadImagesByPaths` | - | - | 写 `uploads/` |
| 追加图片 | `AppendImagesToRecord` | - | - | 写 `uploads/` |
| 删除整条记录 | `DeleteUploadRecord` | - | `AdminDeleteUploadRecord` | 删 `uploads/` |
| 删除单张图片 | `RemoveImageFromRecord` | - | `RemoveImageFromRecord` | 删 `uploads/` |
| 批量移除图片 | - | `RemoveImagesFromRecord`（审核确认后） | `AdminRemoveImagesFromRecord` | 删 `uploads/` |
| 标记完成 | `MarkUploadRecordCompleted`（管理员分配后） | - | `AdminCompleteUploadReview` | - |
| 标记退回 | - | `MarkUploadRecordAsReturned`（驳回后） | - | - |
| 审核后删除 | - | `DeleteUploadRecordAfterReview`（确认后） | `LegacyClean` 中也调 | 条件删（无图片时） |
| 旋转图片 | `RotateUploadImage` | - | `RotateUploadImage` | 覆写 `uploads/` |
| 重置状态 | `ResetUploadRecordStatus` | - | `ResetUploadRecordStatus` | - |
| 创建退回记录 | `CreateReturnedRecord`（驳回后重新入库） | - | - | 不新建 OSS，引用已有路径 |
| 分配为错题 | - | - | `AssignUploadRecord` | 不动 OSS |

### 2.2 对 MistakeItem 的操作

| 操作 | 学生端 | 教师端 | 管理端 | OSS 影响 |
|------|--------|--------|--------|---------|
| 创建（提交） | - | - | `SubmitMistakeUpload`（分配时） | 不动 OSS |
| 教师手动添加 | - | `AddMistakeItem` | - | 不动 OSS |
| 单条审核 | - | `ReviewMistakeItem` | - | **确认时迁移 `uploads/` → `mistakes/`** |
| 批量审核 | - | `BatchReview` | - | 同上 |
| 完成整批审核 | - | `CompleteUploadReview` | `LegacyClean` 中调 | **统一迁移 + 清理** |
| 退回 | - | `ReturnMistake` | - | 不动 OSS |
| 删除 | `DeleteMistake`（仅未确认） | 驳回后自动删 | - | **不清理 OSS** |
| 更新 | - | - | `UpdateMistakeItem` | - |
| 图片迁移 | - | - | `MigrateImages` | `uploads/` → `mistakes/` |

## 3. 问题分析

### 3.1 同一图片路径被双重引用，删除逻辑冲突

一张图片的路径 `uploads/studentId/.../upload_0.jpg` 同时存在于：
- `ImageUploadRecordEntity.ImagePaths`（student 服务）
- `MistakeItemEntity.SourceRegions[].SourceImagePath`（mistake 服务）

冲突场景：

| 场景 | 操作方 | 后果 |
|------|--------|------|
| 学生删除上传记录中的图片 | 学生端 `RemoveImageFromRecord` | 删了 `uploads/` 下的 OSS 文件，但 MistakeItem 还引用这个路径 → 错题图片 404 |
| 教师审核确认，图片迁移到 `mistakes/` | 教师端 `ReviewMistakeItem` | MistakeItem 路径更新为 `mistakes/`，但 UploadRecord 还持有 `uploads/` 路径 → UploadRecord 指向已删除文件 |
| 管理员分配后标记完成，但图片还在 `uploads/` | 管理端 `AssignUploadRecord` | UploadRecord 状态变为 Completed，但图片还在 `uploads/`，审核确认后才迁移 → 中间状态不一致 |

**根本原因**：两个微服务各自管理自己的数据，但共享同一组 OSS 文件，没有统一的"图片所有权"概念。

### 3.2 OSS 迁移逻辑三处重复，且职责放错了层

同一套"uploads/ → mistakes/ 复制+删除+更新路径"逻辑出现在：

| 位置 | 触发场景 |
|------|---------|
| `MistakeServiceImpl.ReviewMistakeItem` | 单条审核确认时 |
| `MistakeServiceImpl.CompleteUploadReview` | 完成整批审核时 |
| `AdminMistakeController.MigrateImages` | 管理员手动迁移 |

三处代码几乎相同但细节不同（pathMapping 缓存、错误处理策略），容易产生不一致。

### 3.3 跨服务编排逻辑散落在 Controller 层，且大量重复

教师端 Controller 的 `ReviewItem` 和 `BatchReview` 中，审核后的后续操作完全相同，但写了两遍：

**审核确认后**：
1. 调 gRPC `ReviewMistakeItem` → 迁移图片
2. 调 gRPC `RemoveImagesFromRecord`（student 服务）
3. 调 gRPC `DeleteUploadRecordAfterReview`（student 服务）

**审核驳回后**：
1. 调 gRPC `ReviewMistakeItem`
2. 调 gRPC `DeleteMistakeItem`
3. 查剩余 items → 如果为空调 gRPC `MarkUploadRecordAsReturned`

管理端 `LegacyClean` 也有一套几乎相同的编排。这些编排逻辑不应该在 Controller 里，更不应该重复写。

### 3.4 删除操作不清理 OSS，导致孤儿文件

| 删除场景 | OSS 清理 | 问题 |
|---------|---------|------|
| 驳回后删除 MistakeItem | 不清理 `uploads/` 下的图片 | 图片变成孤儿 |
| 学生端删除未确认错题 | 不清理 OSS | 同上 |
| `DeleteMistakeItemAsync` | 不清理 `mistakes/` 下的图片 | 已迁移的图片变成孤儿 |
| `RemoveImagesFromRecordAsync` | 清理 `uploads/` 下的图片 | 但如果图片已被迁移到 `mistakes/`，这里删的是旧路径，可能已不存在 |

### 3.5 管理端 Assign 后的中间状态无人管

管理端 `AssignUploadRecord` 的流程是：
1. 按图片分组调用 `SubmitMistakeUpload` 创建 MistakeItem
2. 调 `MarkUploadRecordCompleted` 标记上传记录完成

但此时：
- 图片还在 `uploads/` 下
- MistakeItem 的 `SourceRegions` 指向 `uploads/` 路径
- UploadRecord 状态是 Completed，但 `ImagePaths` 还保留着所有路径
- 如果学生此时删除上传记录 → MistakeItem 引用的图片被删

而且 `AssignUploadRecord` 支持按图片索引分组分配，同一张图片可能被分到多个 MistakeItem（不同 BoundingBox），但 `assignedImagePaths` 只做了去重统计，没有从 UploadRecord 中移除已分配的图片。

### 3.6 驳回流程的 OSS 清理缺失

教师驳回后的流程：
1. `ReviewMistakeItem`（status=Rejected）
2. `DeleteMistakeItem`
3. 检查剩余 items → 如果为空，`MarkUploadRecordAsReturned`

但 `DeleteMistakeItem` 不清理 OSS 文件。如果图片还在 `uploads/` 下（未迁移），UploadRecord 仍持有路径，OSS 文件不会丢失——但如果图片已被迁移到 `mistakes/`（比如之前确认了同上传记录下的另一个 item），此时 `mistakes/` 下的文件就成了孤儿。

### 3.7 LegacyClean 暴露了流程设计的根本缺陷

管理端的 `LegacyClean` 接口的存在本身就说明：正常流程中经常出现"审核完了但上传记录没清理"的情况。它的逻辑是：
1. `CompleteUploadReview` → 迁移图片 + 返回 RemovedImagePaths
2. `RemoveImagesFromRecord` → 从 UploadRecord 移除图片
3. `DeleteUploadRecordAfterReview` → 删除 UploadRecord

这和教师端 `CompleteReview` 的逻辑完全一样，但写在了两个不同的 Controller 里。更关键的是，`LegacyCheck` 还要检查"图片是否还在 `uploads/` 路径下"——这说明系统缺乏对图片当前所处阶段的明确追踪。

### 3.8 单条审核 vs 批量完成的两阶段设计混乱

当前存在两条审核路径：

**路径 A**：逐条审核 + 自动迁移（`ReviewMistakeItem`）
- 审核确认时立即迁移图片
- 但不清理 UploadRecord

**路径 B**：逐条审核 + 最后完成（`ReviewMistakeItem` + `CompleteUploadReview`）
- 逐条审核时迁移图片
- 完成时再迁移一遍（跳过已迁移的）+ 清理 UploadRecord

问题在于：路径 A 中审核确认后图片已迁移到 `mistakes/`，但 UploadRecord 还保留着旧路径。如果此时教师驳回同一 UploadRecord 下的另一个 item，驳回逻辑会删除 MistakeItem，但该 item 引用的图片可能还在 `uploads/`——而 UploadRecord 仍然持有这些路径。

### 3.9 学生端 CreateReturnedRecord 的路径混乱

当教师驳回所有错题后，`MarkUploadRecordAsReturned` 将 UploadRecord 状态改为 Returned。学生重新看到这条记录后可以追加图片重新提交。

但如果学生删除了这条退回的记录，又通过 `CreateReturnedRecord` 重新创建——新记录的 `ImagePaths` 可能指向 `mistakes/` 下的路径（如果之前有图片被迁移过），也可能指向 `uploads/` 下的路径。这个方法不校验路径前缀，直接存储。

## 4. 修改方向

### 方向 A：引入"图片所有权"状态机

为 OSS 文件定义明确的所有权阶段：

```
staging/  →  uploads/  →  mistakes/  →  (删除)
(临时)       (归UploadRecord)  (归MistakeItem)
```

规则：
- 图片在 `uploads/` 时，所有权归 UploadRecord，只有 student 服务可以删除
- 图片在 `mistakes/` 时，所有权归 MistakeItem，只有 mistake 服务可以删除
- **所有权转移是原子的**：迁移成功 + 路径更新 = 所有权转移完成
- 所有权转移后，原持有者（UploadRecord）必须移除引用

这样每个服务只删除自己拥有所有权的文件，不会误删。

### 方向 B：审核两阶段分离

**阶段 1：逐条审核**（`ReviewMistakeItem`）
- 只修改 `ReviewStatus`，不做 OSS 迁移
- MistakeItem 的 `SourceImagePath` 仍然指向 `uploads/`

**阶段 2：完成审核**（`CompleteUploadReview`）
- 统一迁移所有 Confirmed item 的图片 `uploads/` → `mistakes/`
- 统一删除所有 Rejected item 的 MistakeItem 记录
- 从 UploadRecord 移除所有已迁移/已删除的图片引用
- 如果 UploadRecord 无剩余图片，删除 UploadRecord

好处：
- OSS 操作集中在一个点，不存在中间状态
- 学生在审核期间删除上传记录的图片 → 可以在完成审核时检测到路径无效并跳过
- 不再需要 `LegacyClean`

### 方向 C：MistakeItem 删除时清理 mistakes/ 下的 OSS

当前 `DeleteMistakeItemAsync` 只删数据库记录。应该：
- 如果 `SourceImagePath` 以 `mistakes/` 开头 → 删除 OSS 文件
- 如果以 `uploads/` 开头 → 不删除（所有权归 UploadRecord）

### 方向 D：Assign 后锁定 UploadRecord

管理端 `AssignUploadRecord` 后，应该将 UploadRecord 状态改为"已分配/审核中"，禁止学生端删除或修改该记录及其图片。这可以通过在 `DeleteUploadRecordAsync` / `RemoveImageFromRecordAsync` 中增加状态检查来实现（当前已有 Processing 状态的检查，可以扩展）。

### 方向 E：统一跨服务编排逻辑

当前教师端 Controller 和管理端 Controller 各自手写了一套跨服务编排。建议：

在 mistake 服务中新增一个 `CompleteUploadReviewWithCleanup` gRPC 方法，内部编排：
1. 迁移 Confirmed items 的图片
2. 删除 Rejected items
3. 调用 student 服务的 `RemoveImagesFromRecord` + `DeleteUploadRecordAfterReview`
4. 返回结果

这样教师端和管理端都只需调这一个方法，消除重复。

## 5. 优先级建议

| 优先级 | 方向 | 理由 |
|--------|------|------|
| **P0** | B. 审核两阶段分离 | 消除中间状态不一致，是其他改动的基础 |
| **P0** | D. Assign 后锁定 UploadRecord | 防止学生端误删正在审核的图片 |
| **P1** | A. 图片所有权状态机 | 为所有删除操作提供判断依据 |
| **P1** | E. 统一跨服务编排 | 消除重复代码，减少出错概率 |
| **P2** | C. 删除时清理 mistakes/ | 防止孤儿文件，但不影响功能正确性 |
