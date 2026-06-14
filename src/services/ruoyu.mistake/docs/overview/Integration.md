# ruoyu.mistake 集成接口清单

## 集成总表

| # | 交互点 | 接口类型 | 方向 | 对端系统 | 触发场景 |
|---|--------|----------|------|----------|----------|
| 1 | SubmitMistakeUpload | gRPC | 入 | ruoyu.student | 学生上传图片后自动分发创建错题 |
| 2 | GetMistakeItem | gRPC | 入 | admin_portal, teacher_portal, user_portal | 按ID查询单条错题 |
| 3 | GetMistakeItemsByUpload | gRPC | 入 | admin_portal, teacher_portal | 按上传批次查询错题列表 |
| 4 | GetMistakeItemList | gRPC | 入 | admin_portal, teacher_portal, user_portal | 分页查询错题列表 |
| 5 | GetPendingReviewUploads | gRPC | 入 | teacher_portal | 查询待审核上传批次 |
| 6 | ReviewMistakeItem | gRPC | 入 | teacher_portal | 审核单条错题（确认/驳回） |
| 7 | AddMistakeItem | gRPC | 入 | teacher_portal | 手动添加错题条目 |
| 8 | UpdateMistakeItem | gRPC | 入 | admin_portal | 更新错题条目 |
| 9 | DeleteMistakeItem | gRPC | 入 | teacher_portal, user_portal | 删除错题条目 |
| 10 | CompleteUploadReview | gRPC | 入 | teacher_portal | ~~完成整批审核~~ **已废弃**，仅保留供 LegacyClean 运维兜底 |
| 11 | AddMistakePractice | gRPC | 入 | — | 添加练习记录 |
| 12 | GetMistakePractices | gRPC | 入 | — | 查询练习记录 |
| 13 | LogMistakeProcess | gRPC | 入 | — | 记录处理日志 |
| 14 | GetMistakeProcessHistory | gRPC | 入 | — | 查询处理历史 |
| 15 | GetAllReferencedImagePaths | gRPC | 入 | admin_portal | 获取所有引用图片路径（OSS审计） |
| 16 | StudentLearningGrpcServiceClient.MigrateImagesToMistake | gRPC | 出 | ruoyu.student | 图片迁移：uploads/ → mistakes/（替代直接 OSS CopyObject+DeleteObject） |
| 17 | GetPresignedUrl | gRPC | 入 | user_portal, teacher_portal | 生成 mistakes/ 路径图片的预签名 URL |
| 18 | StudentLearningGrpcServiceClient.RemoveImagesFromRecord | gRPC | 出 | ruoyu.student | 审核确认时从上传记录移除已迁移图片 |
| 19 | StudentLearningGrpcServiceClient.MarkUploadRecordAsReturned | gRPC | 出 | ruoyu.student | 上传记录下无待审核条目时标记为已退回 |
| 20 | EF Core (Npgsql/Sqlite) | 数据库 | 出 | PostgreSQL/SQLite | 所有数据持久化操作 |

## 失败语义总结

本服务调用外部系统失败时的降级策略：

| 外部系统 | 失败场景 | 降级策略 | 影响范围 |
|----------|----------|----------|----------|
| **Student** (MigrateImagesToMistake) | ReviewMistakeItem 确认时迁移图片失败 | catch 后 log error，继续处理下一张图 | 该图片未迁移到 mistakes/，source_regions 路径未更新 |
| **Student** (RemoveImagesFromRecord) | ReviewMistakeItem 确认后调用失败 | catch 后 log error，审核状态已持久化不受影响 | 上传记录中仍保留已迁移图片引用，需人工或补偿任务清理 |
| **Student** (MarkUploadRecordAsReturned) | 无待审核条目时标记上传记录失败 | catch 后 log error，审核状态已持久化不受影响 | 上传记录状态未更新为已退回，需人工或补偿任务处理 |
| **PostgreSQL/SQLite** | 数据库操作失败 | EF Core 抛异常，gRPC 返回 INTERNAL 状态码 | 整个请求失败，调用方可重试 |

### 关键风险：ReviewMistakeItem 的非事务性

ReviewMistakeItem 涉及多步外部操作（Student MigrateImagesToMistake → student RemoveImagesFromRecord → student MarkUploadRecordAsReturned），**不具备事务性**：

1. 部分图片迁移成功部分失败 → 已迁移图片路径已更新，未迁移图片路径未更新
2. 图片迁移成功但 student gRPC 调用失败 → 审核状态已持久化，但上传记录未更新（需补偿）

**设计原则**: 审核状态持久化是业务关键节点，不因外部依赖临时不可用而回滚。调用方需设计补偿机制。

### ~~CompleteUploadReview 的非事务性~~（已废弃）

> CompleteUploadReview 已废弃（标记 `[Obsolete]`），仅保留供 LegacyClean 运维兜底。原 CompleteUploadReview 的图片迁移和上传记录更新逻辑已由 ReviewMistakeItem 逐条审核时即时处理。
