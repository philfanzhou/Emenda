# Human Review

## 1. 仍需人工确认事项

| ID | 问题 | 位置 | 状态 | 批复 |
|----|------|------|------|------|
| HR-01 | `process_log.student_id` 迁移在已有生产库上的部署后确认；DatabaseInitializer 已做补 stamp，但需在目标环境确认迁移记录与表结构一致 | DatabaseInitializer.cs | 待确认 | |
| HR-04 | CompletedRetentionDays 配置键命名与语义不一致，保留旧命名以兼容，后续版本可考虑重命名 | — | 待确认 | |
| HR-05 | CompleteUploadReview 标记 Obsolete 但未删除，LegacyClean 仍依赖，后续版本可考虑移除 | — | 待确认 | |
| HR-06 | OSS 迁移与 DB 更新不在同一事务，若 DB 更新失败 OSS 侧文件已迁移导致数据不一致（P0） | MistakeServiceImpl | 待修复 | |
| HR-08 | 多处方法在循环内逐条查询数据库，存在 N+1 查询性能问题（P1） | — | 待修复 | |
| HR-09 | 业务代码与异常消息中存在中文，与项目英文规范不一致（P1） | — | 待修复 | |
| HR-10 | 部分文件包含多个类型定义，不符合单一职责原则（P1） | — | 待修复 | |
| HR-11 | 存在未被调用的方法、未使用的字段或已注释掉的代码块（P2） | — | 待修复 | |
