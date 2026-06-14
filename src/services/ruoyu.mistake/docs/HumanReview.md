# Human Review

## 1. 仍需人工确认事项

- **HR-01: `process_log.student_id` 迁移在已有生产库上的部署后确认**
  - **已核查证据**：`DatabaseInitializer.cs` 已在启动阶段根据 `process_log` 表是否已存在 `student_id` 列，为 `AddStudentIdToProcessLog` 执行补 stamp；旧审计记录中对应 bug 已完成代码修复与文档同步。
  - **为何仍需人工**：当前仓库内无法直接验证真实生产或历史数据库实例上的部署结果，仍需在实际目标环境确认 `__EFMigrationsHistory` 与现有表结构状态匹配。
  - **建议人工下一步**：在目标环境部署后确认服务可正常启动，并检查 `__EFMigrationsHistory` 中 `AddStudentIdToProcessLog` 记录与 `process_log.student_id` 列状态一致；若环境已存在该列，确认不会再次触发重复加列错误。

- **HR-02: Mistake 服务新增 Student.Contract gRPC 客户端依赖的部署确认**
  - **来源**：三元素生命周期重构（AgentGeneralChangeAudit）
  - **说明**：Mistake 服务新增了对 Student.Contract 的项目引用和 gRPC 客户端依赖，部署时需确保 Student 服务可用。

- **HR-03: UploadStatus 枚举值 3 语义变更的数据兼容性**
  - **来源**：三元素生命周期重构（AgentGeneralChangeAudit）
  - **说明**：UploadStatus 枚举值 3 的语义从"已完成"变为"审核中"（Completed→UnderReview），数据库中已有的 status=3 记录含义已变，需确认前端展示逻辑是否需要适配。

- **HR-04: CompletedRetentionDays 配置键命名与语义不一致**
  - **来源**：三元素生命周期重构（AgentGeneralChangeAudit）
  - **说明**：保留旧命名以兼容已有配置文件，但语义已对应 UnderReview 状态，后续版本可考虑重命名。

- **HR-05: CompleteUploadReview 标记 Obsolete 但未删除**
  - **来源**：三元素生命周期重构（AgentGeneralChangeAudit）
  - **说明**：LegacyClean 仍依赖 CompleteUploadReview，当前仅标记 Obsolete，后续版本可考虑移除。

- **HR-06: OSS 迁移与 DB 更新不在同一事务（P0）**
  - **来源**：代码审查（CodeReview）
  - **位置**：`MistakeServiceImpl`
  - **说明**：`CompleteUploadReview` 中 OSS 文件迁移与数据库状态更新不在同一事务内，若 DB 更新失败，OSS 侧文件已迁移但 DB 仍指向旧路径，导致数据不一致。建议将 OSS 迁移与 DB 更新纳入同一事务或引入补偿机制。

- **HR-07: RemoveImagesFromRecord 向 OSS 传递迁移前路径（P0）**
  - **来源**：代码审查（CodeReview）
  - **位置**：`MistakeServiceImpl.RemoveImagesFromRecord`
  - **说明**：该方法将迁移前的旧路径传给 OSS 删除接口，而非迁移后的新路径，导致 OSS 侧无法正确删除已迁移的文件。需在删除前将路径映射为迁移后的 OSS 路径。

- **HR-08: 多处方法存在 N+1 查询问题（P1）**
  - **来源**：代码审查（CodeReview）
  - **说明**：多个方法在循环内逐条查询数据库，存在 N+1 查询性能问题。建议批量预加载关联数据或使用 JOIN 查询替代逐条访问。

- **HR-09: 代码中存在中文注释与消息（P1）**
  - **来源**：代码审查（CodeReview）
  - **说明**：业务代码与异常消息中存在中文，与项目整体英文规范不一致。建议统一为英文注释与消息，或按项目约定处理国际化。

- **HR-10: 同一文件中定义多个类型（P1）**
  - **来源**：代码审查（CodeReview）
  - **说明**：部分文件包含多个类型定义（如服务类与辅助类混放），不符合单一职责原则，影响可维护性。建议拆分为独立文件。

- **HR-11: 死代码（P2）**
  - **来源**：代码审查（CodeReview）
  - **说明**：存在未被调用的方法、未使用的字段或已注释掉的代码块，建议清理以减少维护负担。
