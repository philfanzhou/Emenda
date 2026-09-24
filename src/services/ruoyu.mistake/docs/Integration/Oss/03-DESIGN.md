# Mistake 与 OSS 集成 — 设计说明

## OSS 集成架构

```
┌─────────────────────────────────────────────────────────┐
│                    Mistake 模块                          │
│                                                         │
│  MistakeServiceImpl          MistakeDomainService       │
│  ├─ ReviewMistakeItem()      ├─ GetAllReferencedImagePaths() │
│  │  └─ 图片迁移（通过        │  └─ 返回全量引用路径      │
│  │     Student gRPC          │                           │
│  │     MigrateImagesToMistake│                           │
│  │     替代直接 OSS 操作）   │                           │
│  │                           │                           │
│  ├─ GetPresignedUrl()        │                           │
│  │  └─ 直连 IOssService +   │                           │
│  │     ThumbnailHelper       │                           │
│  │     (oss-path-autonomy)   │                           │
│  └──────── Student gRPC ─────┘                          │
│             │                                           │
└─────────────┼───────────────────────────────────────────┘
              │
     ┌────────┴────────┐
     │                 │
┌────▼─────┐    ┌──────▼──────┐
│Student    │   │IOssService  │
│MigrateImag│   │(Mistake直连)│
│esToMistake│   │+Thumbnail   │
└───────────┘   │Helper       │
                └─────────────┘
                          ▲
                          │
┌─────────────────────────┼─────────────────────────────┐
│                 Ruoyu.Admin                           │
│                                                        │
│  OssAuditWorker                                        │
│  ├─ 调用 Mistake.GetAllReferencedImagePaths            │
│  ├─ 调用 Student.GetRegisteredOssPaths                 │
│  └─ 交叉比对 → 僵尸对象检测                            │
└─────────────────────────────────────────────────────────┘
```

> **重要变更（oss-path-autonomy）**：Mistake 服务恢复直连 OSS。`GetPresignedUrl` 直接调用 `IOssService` + `ThumbnailHelper`，不再代理到 Student gRPC。路径前缀校验仅允许 `mistakes/`。`IOssService.DeleteAsync` 自动清理关联缩略图。

> **图片迁移仍通过 Student gRPC**：Mistake 服务不再直接操作 OSS 的 CopyObject/DeleteObject，图片迁移仍通过 Student gRPC `MigrateImagesToMistake` 代理。

> `OssOptions.InternalEndpoint` 用于实际 S3 操作；`OssOptions.PublicBaseUrl` 用于按公共 authority 生成预签名地址。浏览器通过 User Web Nginx 的 `/oss/` 入口访问对象，Mistake 服务不调用该 Nginx。

## ReviewMistakeItem 中的迁移流程

代码位置：`MistakeServiceImpl.cs`

```
ReviewMistakeItem(id, reviewStatus, reviewerId, ...)
│
├─ 领域层：更新审核状态、记录 ProcessLog
│
├─ if (Confirmed):
│    遍历 SourceRegions
│      收集所有 SourceImagePath → removedPaths
│      若以 "uploads/" 开头:
│        收集到 sourcePaths 列表
│    若 sourcePaths 非空:
│      调用 StudentLearningGrpcServiceClient.MigrateImagesToMistakeAsync(sourcePaths)
│      遍历迁移结果:
│        成功 → 更新 region.SourceImagePath = newPath
│        失败 → 保留原路径，记录 Error 日志
│    若有路径更新 → UpdateMistakeItemAsync(item)
│    调用 StudentLearningGrpcServiceClient.RemoveImagesFromRecord(sourceUploadId, removedPaths)
│
├─ if (Rejected):
│    删除该 MistakeItem
│
└─ (Confirmed 和 Rejected 共用):
     查询该 sourceUploadId 下是否还有 PendingReview 状态的条目
       若无 → 调用 StudentLearningGrpcServiceClient.MarkUploadRecordAsReturned(sourceUploadId)
```

## 路径映射逻辑

迁移时的路径转换由 Student 服务 `MigrateImagesToMistake` 处理，Mistake 服务只需根据返回结果更新路径。

**示例**：

| 原路径 | 新路径 |
|--------|--------|
| `uploads/math/1001/2026/06/09/m001/original_0.jpg` | `mistakes/math/1001/2026/06/09/m001/original_0.jpg` |

## 失败处理策略

### MigrateImagesToMistake 单文件失败

- Student gRPC 返回部分成功结果
- 已迁移的图片路径更新为 `mistakes/` 前缀
- 未迁移的图片保留 `uploads/` 前缀，记录 Error 日志
- 其他图片继续正常迁移
- 审核完成状态照常保存

### MigrateImagesToMistake 完全失败

- gRPC 调用抛出 RpcException
- 所有图片迁移失败，但审核完成状态不会回滚
- 结果：mistake_item 中的图片路径仍指向 `uploads/` 前缀
- 需人工介入修复或重新触发迁移

### 设计权衡

| 决策 | 原因 |
|------|------|
| 单个失败不阻塞 | 审核完成是业务关键操作，图片可后续修复 |
| 迁移成功即更新路径 | 目标文件已存在，路径应指向新位置 |
| 不回滚审核状态 | 审核状态变更涉及多方通知，回滚代价高 |
| 不直接操作 OSS（迁移） | Mistake 图片迁移通过 Student gRPC 代理，但 GetPresignedUrl 直连 IOssService（oss-path-autonomy 正式决策） |

## OssAuditWorker 审计流程

代码位置：`Ruoyu.Admin\backend\Services\OssAuditWorker.cs`

```
OssAuditWorker.ExecuteAsync()
│
├─ 获取 Mistake 引用路径
│  └─ mistakeClient.GetAllReferencedImagePaths()
│     → mistakePaths: Set<string>
│
├─ 获取 Student 引用路径
│  └─ studentClient.GetRegisteredOssPaths()
│     → studentPaths: Set<string>
│
├─ 合并有效路径集合
│  └─ validPaths = mistakePaths ∪ studentPaths
│
├─ 列出 OSS 存储桶中所有对象
│  └─ allObjects: Set<string>
│
└─ 计算僵尸对象
   └─ orphanedObjects = allObjects - validPaths
      → 可安全删除的僵尸对象列表
```
