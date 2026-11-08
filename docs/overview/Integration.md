# ruoyu.mistake 集成接口清单

> **消费方迁出说明（2026-09-24）**：本文中的 `Ruoyu.Admin`（原 `admin_portal` / Admin Portal）已于 [ADR-0010](../../../../../docs/adr/0010-extract-ruoyu-admin.md) 迁出为独立开源仓库 [Ruoyu.Admin](https://github.com/philfanzhou/Ruoyu.Admin)。**本文件描述的端点契约仍由本仓库该服务主责**，消费方改为外部仓库；契约变更不会在该仓库产生编译错误（它持有手写镜像 DTO），因此变更时必须把 Ruoyu.Admin 视为受影响方并同步通知。 **停用说明（2026-11-07）**：Ruoyu.Admin 已按 [ADR-0030](../../../../../docs/adr/0030-portal-hosted-administration.md) 停用。本文中由它发起的管理调用改由 Portal 管理端发出（User Portal 管理端：学生档案、Account Mapping、开放学科、上传记录；Teacher Portal 管理端：错题查看与修改；Assistant Portal 管理端：助教），其余调用（如存储审计引用快照）已无调用方。
>
> **协议描述已过期**：本文中以 gRPC 描述的交互在实际实现中已改为 HTTP/JSON，gRPC 已整体移除。这是迁出前就已存在的文档漂移，尚未逐条重新核对；当前实现事实以本服务的 `Controllers/` 与路由配置为准。


## 集成总表

| # | 交互点 | 接口类型 | 方向 | 对端系统 | 触发场景 |
|---|--------|----------|------|----------|----------|
| 1 | SubmitMistakeUpload | gRPC | 入 | ruoyu.student | 学生上传图片后自动分发创建错题 |
| 2 | GetMistakeItem | gRPC | 入 | Ruoyu.Admin, teacher_portal, user_portal | 按ID查询单条错题 |
| 3 | GetMistakeItemsByUpload | gRPC | 入 | Ruoyu.Admin, teacher_portal | 按上传批次查询错题列表 |
| 4 | GetMistakeItemList | gRPC | 入 | Ruoyu.Admin, teacher_portal, user_portal | 分页查询错题列表 |
| 5 | GetPendingReviewUploads | gRPC | 入 | teacher_portal | 查询待审核上传批次 |
| 6 | ReviewMistakeItem | gRPC | 入 | teacher_portal | 审核单条错题（确认/退回） |
| 7 | AddMistakeItem | gRPC | 入 | teacher_portal | 手动添加错题条目 |
| 8 | UpdateMistakeItem | gRPC | 入 | Ruoyu.Admin | 更新错题条目 |
| 9 | DeleteMistakeItem | gRPC | 入 | teacher_portal, user_portal | 删除错题条目 |
| 10 | CompleteUploadReview | gRPC | 入 | teacher_portal | ~~完成整批审核~~ **已废弃**，仅保留供 LegacyClean 运维兜底 |
| 11 | AddMistakePractice | gRPC | 入 | — | 添加练习记录 |
| 12 | GetMistakePractices | gRPC | 入 | — | 查询练习记录 |
| 13 | LogMistakeProcess | gRPC | 入 | — | 记录处理日志 |
| 14 | GetMistakeProcessHistory | gRPC | 入 | — | 查询处理历史 |
| 15 | GetAllReferencedImagePaths | gRPC | 入 | Ruoyu.Admin | 获取所有引用图片路径（OSS审计） |
| 16 | StudentLearningGrpcServiceClient.MigrateImagesToMistake | gRPC | 出 | ruoyu.student | 图片迁移：uploads/ → mistakes/（替代直接 OSS CopyObject+DeleteObject） |
| 17 | GetPresignedUrl | gRPC | 入 | user_portal, teacher_portal | 生成 mistakes/ 路径图片的预签名 URL；Mistake 恢复直连 OSS（oss-path-autonomy），直接调用 `IOssService` + `ThumbnailHelper`，不再代理到 Student gRPC；路径前缀校验仅允许 `mistakes/`；支持缩略图生成（当 `size` 参数不为空且不为 `full` 时触发）：尺寸映射 `full`=原图, `medium`=800px, `small`=400px, `thumbnail`=200px；缩放模式 `ResizeMode.Max`（保持宽高比，长边不超过指定像素）；缩略图与原图同目录，路径格式 `{dirname}/{stem}_{size}.jpg`（示例：原图 `mistakes/2025/06/14/def/image.jpg` → 缩略图 `mistakes/2025/06/14/def/image_small.jpg`）；缓存命中直接返回预签名 URL，缓存未命中则下载原图 → Common GuardedImageDecoder 缩放 → 上传到 OSS → 返回预签名 URL；降级策略：缩略图生成失败时返回原图 URL + Warning 日志；`IOssService.DeleteAsync` 自动清理关联缩略图 |
| 18 | StudentLearningGrpcServiceClient.RemoveImagesFromRecord | gRPC | 出 | ruoyu.student | 审核确认时从上传记录移除已迁移图片 |
| 19 | StudentLearningGrpcServiceClient.MarkUploadRecordAsReturned | gRPC | 出 | ruoyu.student | 上传记录下无待审核条目时标记为已退回 |
| 20 | EF Core (Npgsql) | 数据库 | 出 | PostgreSQL | 所有数据持久化操作 |

## 失败语义总结

本服务调用外部系统失败时的降级策略：

| 外部系统 | 失败场景 | 降级策略 | 影响范围 |
|----------|----------|----------|----------|
| **Student** (MigrateImagesToMistake) | ReviewMistakeItem 确认时迁移图片失败 | catch 后 log error，继续处理下一张图 | 该图片未迁移到 mistakes/，source_regions 路径未更新 |
| **Student** (RemoveImagesFromRecord) | ReviewMistakeItem 确认后调用失败 | catch 后 log error，审核状态已持久化不受影响 | 上传记录中仍保留已迁移图片引用，需人工或补偿任务清理 |
| **Student** (MarkUploadRecordAsReturned) | 无待审核条目时标记上传记录失败 | catch 后 log error，审核状态已持久化不受影响 | 上传记录状态未更新为已退回，需人工或补偿任务处理 |
| **PostgreSQL** | 数据库操作失败 | EF Core 抛异常，gRPC 返回 INTERNAL 状态码 | 整个请求失败，调用方可重试 |

### 关键风险：ReviewMistakeItem 的非事务性

ReviewMistakeItem 涉及多步外部操作（Student MigrateImagesToMistake → student RemoveImagesFromRecord → student MarkUploadRecordAsReturned），**不具备事务性**：

1. 部分图片迁移成功部分失败 → 已迁移图片路径已更新，未迁移图片路径未更新
2. 图片迁移成功但 student gRPC 调用失败 → 审核状态已持久化，但上传记录未更新（需补偿）

**设计原则**: 审核状态持久化是业务关键节点，不因外部依赖临时不可用而回滚。调用方需设计补偿机制。

### ~~CompleteUploadReview 的非事务性~~（已废弃）

> CompleteUploadReview 已废弃（标记 `[Obsolete]`），仅保留供 LegacyClean 运维兜底。原 CompleteUploadReview 的图片迁移和上传记录更新逻辑已由 ReviewMistakeItem 逐条审核时即时处理。
