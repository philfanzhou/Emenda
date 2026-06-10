# Student 与 Mistake 数据引用集成

## 功能名称和一句话概括

Student-Mistake 数据引用 — `ruoyu.mistake` 通过 `student_id` 只读引用 `ruoyu.student` 的学生数据，不直接调用 student 的 gRPC 接口。

## 所属功能块

- 功能块：`Integration / Student`
- 方向：`student → mistake`（单向 gRPC）；`mistake → student`（仅数据引用，无 gRPC 调用）

## 核心用户故事

1. **自动创建错题**：作为上传分析链路，当 VL 分析将图片分组后，student 服务自动调用 mistake 的 `SubmitMistakeUpload` 创建错题条目。
2. **只读引用学生数据**：作为 mistake 服务，我通过 `student_id` 字段引用学生数据，但不直接调用 student 的 gRPC 接口，也不校验学生是否存在。

## 功能边界

### 方向1: student → mistake (SubmitMistakeUpload)

- **触发条件**：`UploadAnalysisWorker` 完成 VL 分析，且分析结果包含分组（`HasGroups`）
- **调用方**：`GrpcMistakeSubmitter`（实现 `IMistakeSubmitter`）
- **分发方**：`GrpcUploadDispatcher`（实现 `IUploadDispatcher`），提供 `DispatchToMistakeAsync` 和 `IsMistakeServiceAvailableAsync`
- **行为**：遍历每个 group，独立调用 `SubmitMistakeUpload`；单个 group 失败不影响其他 group
- **超时**：30 秒

### 方向2: mistake → student（仅数据引用）

- **引用方式**：mistake 在 `mistake_item` 和 `mistake_practice` 表中存储 `student_id` 字段
- **引用语义**：`student_id` 是只读引用字段，mistake 不校验该 ID 对应的学生是否存在
- **无 gRPC 调用**：mistake 服务不注册任何 student gRPC 客户端，不调用 student 的任何 gRPC 方法
- **数据来源**：`student_id` 由 student 服务在调用 `SubmitMistakeUpload` 时传入，mistake 仅存储

## 核心约束

| 约束 | 说明 |
|------|------|
| SubmitMistakeUpload 非幂等 | 每次调用创建新错题条目，`source_upload_id` 不做唯一性校验 |
| 单 group 隔离 | UploadAnalysisWorker 中每个 group 独立提交，互不影响 |
| student_id 仅引用 | mistake 不校验 `student_id` 是否对应有效学生，仅作为关联字段存储 |
| 无跨服务事务 | mistake 与 student 之间无分布式事务保证 |

## 范围外

- 不实现 Mistake 服务端创建错题的业务逻辑
- 不实现 mistake 主动调用 student gRPC 接口
- 不实现跨服务分布式事务
- 不实现 portal 页面交互
- 不实现重试队列或补偿机制

## 文档索引

- [02-SPEC.md](./02-SPEC.md)
- [03-DESIGN.md](./03-DESIGN.md)
- [04-TASKS.md](./04-TASKS.md)
- [05-TESTS.md](./05-TESTS.md)
- [06-CONVENTIONS.md](./06-CONVENTIONS.md)
