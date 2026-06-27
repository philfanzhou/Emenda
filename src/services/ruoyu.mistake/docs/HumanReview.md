# HumanReview — ruoyu.mistake

> 代码审查发现项，按优先级排列。已解决项必须移除。

## P1 — 应尽快修复

### HR-06: OSS 迁移与 DB 更新不在同一事务

- **问题**：MistakeServiceImpl.ReviewMistakeItem 中 OSS 文件迁移（MigrateImagesToMistake）与 DB 更新不在同一事务，若 DB 更新失败 OSS 侧文件已迁移导致数据不一致
- **A** (推荐)：实现 Saga 补偿：DB 失败时回滚 OSS 迁移（删除已迁移文件）
- **B**：使用 Outbox 模式：先写 DB 事件，异步执行 OSS 迁移
- **C**：暂不处理，当前审核频率低
- **批复**：

## P1 — 应尽快修复

### HR-08: 多处 N+1 查询性能问题

- **问题**：多处方法在循环内逐条查询数据库，存在 N+1 查询性能问题
- **A** (推荐)：批量预加载关联数据，用 EF Core Include 改写
- **B**：编写原生 SQL JOIN 查询
- **C**：暂不处理，当前数据量小
- **批复**：

### HR-09: 业务代码与异常消息中存在中文

- **问题**：业务代码与异常消息中存在中文，与项目英文规范不一致
- **A** (推荐)：统一改为英文，中文仅保留用户可见的 DisplayNames
- **B**：保持中文，在规范中允许 gRPC 错误消息使用中文
- **C**：暂不处理，不影响功能
- **批复**：

### HR-10: 部分文件包含多个类型定义

- **问题**：部分文件包含多个类型定义，不符合单一职责原则
- **A** (推荐)：每个类型拆分到独立文件，遵循 C# 约定
- **B**：按功能分组，每组一个文件
- **C**：暂不处理，不影响功能
- **批复**：

### HR-11: 存在未被调用的方法和死代码

- **问题**：存在未被调用的方法、未使用的字段或已注释掉的代码块
- **A** (推荐)：删除所有死代码，依赖 Git 历史追溯
- **B**：标记 Obsolete，后续版本清理
- **C**：暂不处理，不影响运行
- **批复**：

## P2 — 待确认

### HR-01: process_log.student_id 迁移部署后确认

- **问题**：process_log.student_id 迁移在已有生产库上的部署后确认；DatabaseInitializer 已做补 stamp，但需在目标环境确认迁移记录与表结构一致
- **A** (推荐)：部署后在生产环境执行验证 SQL，确认迁移记录与表结构一致
- **B**：在 DatabaseInitializer 中添加运行时校验断言
- **C**：暂不处理，等部署时确认
- **批复**：

### HR-04: CompletedRetentionDays 配置键命名与语义不一致

- **问题**：CompletedRetentionDays 配置键命名与语义不一致（实际含义为 UnderReview 保留天数），保留旧命名以兼容
- **A** (推荐)：重命名为 UnderReviewRetentionDays，添加配置别名兼容旧名
- **B**：保持旧名，在注释中说明语义变更
- **C**：暂不处理，功能不受影响
- **批复**：

### HR-05: CompleteUploadReview 标记 Obsolete 但未删除

- **问题**：CompleteUploadReview 标记 Obsolete 但未删除，LegacyClean 仍依赖
- **A** (推荐)：确认 LegacyClean 不再使用后删除方法及相关 proto 定义
- **B**：保留但移至 LegacyClean 专用类，从主服务剥离
- **C**：暂不处理，标记 Obsolete 已足够
- **批复**：
