# Mistake 与 OSS 集成 — 设计说明

## OSS 集成架构

```
┌─────────────────────────────────────────────────────────┐
│                    Mistake 模块                          │
│                                                         │
│  MistakeServiceImpl          MistakeDomainService       │
│  ├─ CompleteUploadReview()   ├─ GetAllReferencedImagePaths() │
│  │  └─ 图片迁移              │  └─ 返回全量引用路径      │
│  │                           │                           │
│  └──────── IOssService ──────┘                          │
│             │                                           │
└─────────────┼───────────────────────────────────────────┘
              │
     ┌────────┴────────┐
     │                 │
┌────▼─────┐    ┌──────▼──────┐
│S3OssService│   │LocalFileOssService│
│(MinIO/    │   │(本地文件系统)     │
│SeaweedFS) │   │                   │
└───────────┘   └───────────────────┘
                          ▲
                          │
┌─────────────────────────┼─────────────────────────────┐
│                 admin_portal                           │
│                                                        │
│  OssAuditWorker                                        │
│  ├─ 调用 Mistake.GetAllReferencedImagePaths            │
│  ├─ 调用 Student.GetRegisteredOssPaths                 │
│  └─ 交叉比对 → 僵尸对象检测                            │
└────────────────────────────────────────────────────────┘
```

## CompleteUploadReview 中的迁移流程

代码位置：`MistakeServiceImpl.cs` L273-336

```
CompleteUploadReview(reviewId, confirmedItems, rejectedItems)
│
├─ 遍历 confirmedItems
│  ├─ 遍历 item 中的每个图片路径
│  │  ├─ 检查 pathMapping 是否已缓存该路径
│  │  │  ├─ 已缓存 → 跳过迁移，使用缓存路径
│  │  │  └─ 未缓存 → 执行迁移
│  │  │     ├─ newPath = "mistakes" + oldPath.Substring("uploads".Length)
│  │  │     ├─ ossService.CopyObjectAsync(oldPath, newPath)
│  │  │     ├─ 更新 region.SourceImagePath = newPath
│  │  │     ├─ pathMapping[oldPath] = newPath
│  │  │     └─ ossService.DeleteAsync(oldPath) ← 失败仅记录 Warning，不影响路径更新
│  │  └─ 更新 item 中的图片路径为 newPath
│  └─ 持久化 mistake_item
│
├─ 遍历 rejectedItems
│  └─ 不迁移图片，原图保留在 uploads/ 路径
│
└─ 保存审核完成状态
```

## 路径映射逻辑

迁移时的路径转换仅涉及前缀替换：

```csharp
string newPath = "mistakes" + oldPath.Substring("uploads".Length);
```

**示例**：

| 原路径 | 新路径 |
|--------|--------|
| `uploads/math/1001/2026/06/09/m001/original_0.jpg` | `mistakes/math/1001/2026/06/09/m001/original_0.jpg` |
| `uploads/math/1001/2026/06/09/m001/practice_p1_0.jpg` | `mistakes/math/1001/2026/06/09/m001/practice_p1_0.jpg` |

**路径缓存**：`pathMapping` 字典确保同一图片路径只迁移一次，避免重复复制和删除操作。

## 失败处理策略

### Copy 失败

- 捕获异常后记录日志，不抛出、不阻塞
- 该图片路径不会被更新，保留 `uploads/` 前缀
- 其他图片继续正常迁移
- 审核完成状态照常保存

### Copy 成功但 Delete 失败

- Delete 异常被单独捕获，仅记录 Warning 日志
- 图片路径**已更新**为 `mistakes/` 前缀（因为 Copy 已成功，目标文件已存在）
- OSS 中同时存在 `uploads/` 和 `mistakes/` 的副本（源文件未被删除）
- 不影响业务流程，残留的 `uploads/` 副本可由 OssAuditWorker 清理

### OSS 完全不可用

- `CopyObjectAsync` 抛出异常
- 所有图片迁移失败，但审核完成状态不会回滚
- 结果：mistake_item 中的图片路径仍指向 `uploads/` 前缀
- 需人工介入修复或重新触发迁移

### 设计权衡

| 决策 | 原因 |
|------|------|
| 单个失败不阻塞 | 审核完成是业务关键操作，图片可后续修复 |
| Copy 成功即更新路径 | 目标文件已存在，路径应指向新位置；残留源文件由审计清理 |
| Delete 失败仅记录 Warning | 源文件残留不影响业务，可由 OssAuditWorker 清理 |
| 不回滚审核状态 | 审核状态变更涉及多方通知，回滚代价高 |
| 全量返回引用路径 | 审计场景需要完整数据集，分页增加调用复杂度 |

## OssAuditWorker 审计流程

代码位置：`admin_portal\backend\Services\OssAuditWorker.cs`

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
