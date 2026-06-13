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
| 创建（上传图片） | ~~`UploadImages`~~（未使用，待清除）/ `UploadImagesByPaths` | - | - | 写 `uploads/` |
| 追加图片 | `AppendImagesToRecord` | - | - | 写 `uploads/` |
| 删除整条记录 | `DeleteUploadRecord` | - | `AdminDeleteUploadRecord` | 删 `uploads/` |
| 删除单张图片 | `RemoveImageFromRecord` | - | `RemoveImageFromRecord` | 删 `uploads/` |
| 批量移除图片 | - | `RemoveImagesFromRecord`（审核确认后） | `AdminRemoveImagesFromRecord` | 删 `uploads/` |
| 标记审核中 | `MarkUploadRecordCompleted`（→ 重命名为 `MarkUploadRecordUnderReview`，管理员分配后） | - | `AdminCompleteUploadReview`（→ 重命名） | - |
| 标记退回 | - | ~~`MarkUploadRecordAsReturned`~~（教师端 Controller 不再直接调用，改由 mistake 服务 gRPC 层在审核编排中调用） | - | - |
| 审核后删除 | - | ~~`DeleteUploadRecordAfterReview`~~（新流程下由 `RemoveImagesFromRecord` 内部处理，待废弃） | `LegacyClean` 中也调 | 条件删（无图片时） |
| 旋转图片 | `RotateUploadImage`（仅管理端调用） | - | `RotateUploadImage` | 覆写 `uploads/` |
| 重置状态 | `ResetUploadRecordStatus`（仅管理端调用） | - | `ResetUploadRecordStatus` | - |
| ~~创建退回记录~~ | ~~`CreateReturnedRecord`~~（新流程下不需要：驳回后原记录直接标 Returned，无需新建记录；待清除） | - | - | - |
| 分配为错题 | - | - | `AssignUploadRecord` | 不动 OSS |

### 2.2 对 MistakeItem 的操作

| 操作 | 学生端 | 教师端 | 管理端 | OSS 影响 |
|------|--------|--------|--------|---------|
| 创建（提交） | - | - | `SubmitMistakeUpload`（VL 分析自动提交 / 管理员手动分配） | 不动 OSS |
| 教师手动添加 | - | `AddMistakeItem` | - | 不动 OSS |
| 单条审核 | - | `ReviewMistakeItem` | - | **确认时迁移 `uploads/` → `mistakes/`** |
| 批量审核 | - | `BatchReview` | - | 同上 |
| 完成整批审核 | - | `CompleteUploadReview` | `LegacyClean` 中调 | **统一迁移 + 清理** |
| 退回 | - | `ReturnMistake` | - | 不动 OSS |
| 删除 | ~~`DeleteMistake`~~（学生不应能删除未确认错题，待清除） | 驳回后自动删 | - | **不清理 OSS** |
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

## 4. 目标流程

### 4.1 核心原则

1. **审核是逐条即时处理的**：每审核一条 MistakeItem，立即完成该条的所有后续操作（OSS 迁移、路径清理），不存在"先批量审核、再统一完成"的两阶段
2. **不存在独立的 CompleteUploadReview 步骤**：当前代码中的 `CompleteUploadReview` 不符合目标流程，应废弃
3. **student 服务不需要感知 MistakeItem**：审核通过时，mistake 服务调 student 服务的 `RemoveImagesFromRecord` 移除图片，student 服务内部自行决定是否删除记录；审核驳回时，mistake 服务自行检查剩余 item，无待审核则调 `MarkUploadRecordAsReturned`

### 4.2 审核通过流程

教师审核一条 MistakeItem 为 Confirmed 时，按顺序执行：

```
1. 更新 MistakeItem.ReviewStatus = Confirmed
2. 迁移 OSS：该 item 所有图片 uploads/ → mistakes/
3. 更新 MistakeItem.SourceRegions 中的路径为 mistakes/ 前缀
4. 调 student 服务 RemoveImagesFromRecord：移除该 item 包含的图片路径
   → student 服务内部：移除图片路径，若无剩余图片则删除 UploadRecord
5. 检查该 UploadRecord 下是否还有 PendingReview 的 MistakeItem
   → 若无待审核 item 且记录未被删除，调 student 服务 MarkUploadRecordAsReturned
```

注意：
- 步骤 2-3 是原子的：迁移成功 + 路径更新 = 所有权转移完成
- 步骤 4 中 `RemoveImagesFromRecordAsync` 会尝试删除 `uploads/` 下的 OSS 文件（此时图片已迁移到 `mistakes/`，旧文件已不存在，可安全忽略）
- 步骤 4 的返回值 `RecordDeleted` 指示记录是否已被自动删除
- 步骤 5 由 mistake 服务执行，student 服务不需要知道 MistakeItem 的存在

### 4.3 审核驳回流程

教师审核一条 MistakeItem 为 Rejected 时，按顺序执行：

```
1. 更新 MistakeItem.ReviewStatus = Rejected
2. 删除该 MistakeItem 记录（级联删除关联的 practice 和 process_log）
3. 不动 OSS 文件，不调 student 服务
4. 检查该 UploadRecord 下是否还有 PendingReview 的 MistakeItem
   → 若无待审核 item，调 student 服务 MarkUploadRecordAsReturned
```

注意：
- 驳回的 MistakeItem 引用的图片仍在 `uploads/` 下，所有权仍归 UploadRecord，不需要迁移或删除
- 删除 MistakeItem 记录后，这些图片只被 UploadRecord 引用，不会产生双重引用问题
- 驳回不调 `RemoveImagesFromRecord`，因为图片不需要从记录中移除

### 4.4 上传记录状态检查

审核后的状态检查由 mistake 服务在 gRPC 层统一编排，分两部分：

**通过时**：`RemoveImagesFromRecord` 内部处理
```
移除图片路径后：
├─ 无剩余图片 → 删除 UploadRecord（student 服务内部逻辑，无需 mistake 感知）
└─ 有剩余图片 → 保留记录
```

**通过/驳回后**：mistake 服务检查剩余 MistakeItem
```
查询该 UploadRecord 下是否还有 PendingReview 的 MistakeItem：
├─ 有待审核 item → 不动（审核仍在进行中）
└─ 无待审核 item 且记录未被删除 → 调 MarkUploadRecordAsReturned
```

### 4.5 完整流程图

```
UploadRecord（含多张图片）
        │
        ▼ 系统自动识别 / 管理员分配
   创建 MistakeItem(s) → 分配给教师审核
        │
        ▼ 教师逐条审核
   ┌─────┴─────┐
   │            │
 Confirmed    Rejected
   │            │
 ①改状态       ①改状态
 ②迁移 OSS     ②删 MistakeItem
   uploads/→mistakes/   （不动 OSS、不调 student）
 ③更新路径
 ④RemoveImagesFromRecord
   （student 内部：无图片→删记录）
   │            │
   └─────┬──────┘
         ▼
   ⑤检查剩余 PendingReview 的 MistakeItem
   ├─ 有待审核 → 不动
   └─ 无待审核 + 记录仍在 → MarkUploadRecordAsReturned
```

## 5. 当前代码与目标流程的差异

### 5.1 `ReviewMistakeItem` gRPC 实现

**当前行为**（[MistakeServiceImpl.cs:147-230](../src/Service/MistakeServiceImpl.cs)）：
- Confirmed 时：改状态 + 迁移 OSS + 更新路径 + 返回 RemovedImagePaths ✅
- Rejected 时：只改状态，不删 MistakeItem ❌（删除由 Controller 层做）

**目标行为**：
- Confirmed 时：改状态 + 迁移 OSS + 更新路径 + 返回 RemovedImagePaths ✅（当前已正确）
- Rejected 时：改状态 + 删除 MistakeItem 记录（应从 Controller 下沉到 gRPC 服务层）

### 5.2 审核后的上传记录状态检查

**当前行为**：
- Confirmed 后：Controller 调 `RemoveImagesFromRecord` + `DeleteUploadRecordAfterReview`
  - `DeleteUploadRecordAfterReview` 只检查"是否还有图片"，不检查"是否还有待审核 MistakeItem" ❌
  - 如果有图片但没有待审核 MistakeItem，不会标记退回 ❌
- Rejected 后：Controller 检查 `remainingItems.Count == 0` → 标记退回
  - 只检查 MistakeItem 数量，不检查 UploadRecord 是否还有图片 ❌
  - 如果没有图片了，应该删除 UploadRecord 而不是标记退回 ❌

**目标行为**：
- 通过时：mistake 服务调 `RemoveImagesFromRecord`（student 内部无图片则删记录），再检查剩余 item 决定是否调 `MarkUploadRecordAsReturned`
- 驳回时：mistake 服务检查剩余 item，无待审核则调 `MarkUploadRecordAsReturned`
- 不需要新增 gRPC 方法，复用已有的 `RemoveImagesFromRecord` 和 `MarkUploadRecordAsReturned`

### 5.3 `CompleteUploadReview`

**当前行为**（[MistakeServiceImpl.cs:317-418](../src/Service/MistakeServiceImpl.cs)）：
- 存在一个独立的"完成审核"步骤，内部做 OSS 迁移 + 清理
- 与 `ReviewMistakeItem` 中的迁移逻辑重复

**目标行为**：
- 目标流程中不存在独立的 CompleteUploadReview 步骤
- 每条审核通过时立即迁移，不需要二次迁移
- 此方法应废弃或重定义

### 5.4 `LegacyClean`

**当前行为**（[OssUploadRecordController.cs:410-503](../../../../admin_portal/backend/Controllers/OssUploadRecordController.cs)）：
- 管理端手动触发的清理接口，编排逻辑与教师端审核后逻辑重复

**目标行为**：
- 如果每次审核后都正确执行状态检查，不应遗留需要 LegacyClean 的场景
- 此接口可保留为运维兜底，但不应是正常流程的一部分

### 5.5 跨服务编排逻辑重复

**当前行为**：
- 教师端 `ReviewItem`（[MistakeController.cs:539-677](../../../../teacher_portal/backend/Controllers/MistakeController.cs)）和 `BatchReview`（[MistakeController.cs:216-393](../../../../teacher_portal/backend/Controllers/MistakeController.cs)）各自手写编排逻辑
- 管理端 `LegacyClean` 也有一套几乎相同的编排
- 三处逻辑应一致但细节不同

**目标行为**：
- 审核后的完整编排（改状态 + OSS 迁移 + 路径清理 + 状态检查）应下沉到 mistake 服务的 gRPC 层
- Controller 只负责权限校验和调用 gRPC，不编排跨服务逻辑

## 6. 改动项

### 改动 1：`ReviewMistakeItem` gRPC 方法增加驳回后的 MistakeItem 删除

当前 Rejected 后只改状态，删除 MistakeItem 由 Controller 做。应将删除逻辑下沉到 gRPC 服务层，使 `ReviewMistakeItem` 成为完整的单条审核操作。

### 改动 2：新增"上传记录状态检查" gRPC 方法

在 student 服务中新增 `FinalizeUploadRecordAfterReview` gRPC 方法，接收 `recordId` + `studentId` + `hasPendingMistakeItems`，内部执行 4.4 节的状态检查：
- 无图片 → 删除记录
- 有图片 + 无待审核 → 标记退回
- 有图片 + 有待审核 → 不动

参数 `hasPendingMistakeItems` 由 mistake 服务在审核后查询并传入。这样 student 服务不需要反向调用 mistake 服务，依赖方向保持单向：mistake → student。

### 改动 3：`ReviewMistakeItem` gRPC 方法增加审核后的状态检查编排

审核完一条后（不管是通过还是驳回），在 gRPC 服务层统一编排：
1. Confirmed → 迁移 OSS + 从 UploadRecord 移除图片路径
2. Rejected → 删除 MistakeItem
3. 查询该 UploadRecord 下是否还有 PendingReview 的 MistakeItem
4. 调用 student 服务的 `FinalizeUploadRecordAfterReview`

这样教师端和管理端 Controller 只需调用 `ReviewMistakeItem`，不需要再做任何跨服务编排。

### 改动 4：简化 Controller 层

教师端 `ReviewItem` 和 `BatchReview` 中，删除所有审核后的跨服务编排代码（`RemoveImagesFromRecord`、`DeleteUploadRecordAfterReview`、`MarkUploadRecordAsReturned`、`GetMistakeItemsByUpload`），这些逻辑已下沉到 gRPC 服务层。

### 改动 5：废弃 `CompleteUploadReview`

**调用方调查结果**：
- 教师端 `MistakeController.CompleteReview`（`POST /api/mistake/complete-review`）— 后端暴露了 HTTP 端点，但前端代码中**未找到**对该接口的实际调用
- 管理端 `OssUploadRecordController.LegacyClean`（`POST /api/oss-upload-record/legacy-clean/{id}`）— 仅用于遗留数据清理

**处理方案**：
- 目标流程中不存在独立的 CompleteUploadReview 步骤，每条审核通过时立即迁移
- 教师端 `CompleteReview` 端点标记为废弃，前端未使用，可安全移除
- 管理端 `LegacyClean` 保留为运维兜底工具，但不再是正常流程的一部分
- `MistakeServiceImpl.CompleteUploadReview` 和 `MistakeDomainService.CompleteUploadReviewAsync` 标记为废弃
- 相关测试随代码变更同步更新

### 改动 6：重命名 `Completed` 状态为 `UnderReview`，Assign 后锁定 UploadRecord

UploadRecord 不存在"已完成"终态——处理完毕就是删除或退回。当前 `Completed = 3` 的实际含义是"已分配给 MistakeItem，正在等待教师审核"，应重命名为 `UnderReview`。

**目标 `UploadStatus` 枚举值**：

| 值 | 当前名称 | 目标名称 | 含义 |
|----|---------|---------|------|
| 1 | Pending | Pending | 待处理 |
| 2 | Processing | Processing | VL 分析处理中（外部分析服务设置） |
| 3 | Completed | **UnderReview** | 审核中（Assign 后标记，图片已分配给 MistakeItem） |
| 4 | Failed | Failed | 处理失败 |
| 5 | Returned | Returned | 已退回（审核驳回后退回给学生） |

UploadRecord 的生命周期：

```
Pending → Processing → UnderReview → 删除（审核通过，图片全部迁移）
                                     → Returned（审核驳回，退回给学生）
```

**代码变更**：
- `UploadStatus.Completed` → `UploadStatus.UnderReview`（枚举值不变，只改名称）
- `MarkUploadRecordCompletedAsync` → `MarkUploadRecordUnderReviewAsync`
- `AdminCompleteUploadReview` gRPC 方法 → 重命名
- 所有引用 `Completed` 状态的代码同步更新

**锁定逻辑**：
- 当前 `DeleteUploadRecordAsync`、`RemoveImageFromRecordAsync`、`RemoveImagesFromRecordAsync`、`AppendImagesToRecordAsync` 只检查 `Processing` 状态时拒绝操作
- 将 `UnderReview`（原 Completed）也加入拒绝条件：当 UploadRecord 处于 `UnderReview` 状态时，禁止学生端删除记录、删除/追加图片
- 审核全部处理完毕后，由 mistake 服务 gRPC 层编排决定是删除记录（通过 `RemoveImagesFromRecord`）还是标记退回（通过 `MarkUploadRecordAsReturned`），不再由学生端操作

### 改动 7：处理 `LegacyClean`

此接口在目标流程中不应被正常触发。保留为运维兜底，但应在文档中标注其定位变化。

## 7. 依赖关系

改动之间的依赖顺序：

```
改动 6（Assign 后锁定）  ← 可独立实施，优先级最高
        │
改动 1（驳回后删除下沉）
        │
改动 2（不需要新增方法，复用 RemoveImagesFromRecord + MarkUploadRecordAsReturned）
        │
改动 3（ReviewMistakeItem 增加编排）
        │
改动 4（简化 Controller）+ 改动 5（废弃 CompleteUploadReview）
        │
改动 7（LegacyClean 标注定位变化）
```

改动 6 可以独立实施，不依赖其他改动。改动 1-3 有顺序依赖：先下沉驳回删除（1），再新增状态检查方法（2），最后在 ReviewMistakeItem 中编排完整流程（3）。改动 4 和 5 是清理工作，在 1-3 完成后实施。
