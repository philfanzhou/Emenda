# Student 与 Mistake 轮询采集集成

## 功能名称和一句话概括

Student-Mistake 轮询采集 — `ruoyu.mistake` 主动轮询 `ruoyu.student` 的已分类上传记录，采集错题图片进行 VL 分析和错题创建。

## 所属功能块

- 功能块：`Integration / Student`
- 方向：`mistake → student`（单向 gRPC，轮询 + 回调）

## 核心用户故事

1. **轮询采集错题**：作为错题服务，我每分钟轮询 Student 服务的 Processing 状态上传记录，获取标记为 "mistake" 的图片进行 VL 分析。
2. **处理完成通知**：当我完成错题创建和 OSS 迁移后，我通知 Student 移除已处理的图片路径。当所有图片被移除后，Student 自动删除上传记录。
3. **退回创建记录**：当老师审核退回错题时，我创建一条新的 Returned 上传记录，学生可以修改或删除。

## 功能边界

### 方向1: mistake → student（轮询采集）

- **触发条件**：`UploadPollingWorker` 每 60 秒轮询一次
- **调用方**：Mistake 服务的 `UploadPollingWorker`（BackgroundService）
- **目标**：Student 的 `GetProcessingUploadRecords` gRPC 接口
- **行为**：获取所有 Processing 状态的上传记录，过滤 `type == "mistake"` 的图片，进行 VL 分析（含错误根因生成）、错题创建、OSS 迁移

### 方向2: mistake → student（处理完成通知）

- **触发条件**：Mistake 完成错题创建和 OSS 迁移后
- **调用方**：`UploadPollingWorker`
- **目标**：Student 的 `RemoveProcessedImages` gRPC 接口
- **行为**：移除已处理的图片路径。Student 在所有图片被移除后自动删除上传记录

### 方向3: mistake → student（退回创建记录）

- **触发条件**：老师审核退回错题时
- **调用方**：`MistakeServiceImpl.ReviewMistakeItem`（Reject 分支）
- **目标**：Student 的 `CreateReturnedUploadRecord` gRPC 接口
- **行为**：创建一条新的 Returned 状态上传记录，包含从 mistakes/ 迁回 uploads/ 的图片路径

## 核心约束

| 约束 | 说明 |
|------|------|
| 轮询间隔 60 秒 | Mistake 每分钟轮询一次，Processing 记录有最多 60 秒延迟 |
| 单向依赖 | 仅 mistake → student，无 student → mistake 调用 |
| student_id 仅引用 | mistake 不校验 student_id 是否对应有效学生，仅作为关联字段存储 |
| 无跨服务事务 | mistake 与 student 之间无分布式事务保证 |
| 退回记录独立 | 退回时创建的新上传记录与原始记录无关联 |
| 图片分类 | 只处理 type="mistake" 的图片，type="homework" 的图片留给作业服务 |

## 范围外

- 不实现 student 服务的图片分类逻辑（由 Student 自行完成）
- 不实现跨服务分布式事务
- 不实现 portal 页面交互
- 不实现重试队列（轮询机制天然重试）

## 文档索引

- [02-SPEC.md](./02-SPEC.md)
- [03-DESIGN.md](./03-DESIGN.md)
- [04-TASKS.md](./04-TASKS.md)
- [05-TESTS.md](./05-TESTS.md)
- [06-CONVENTIONS.md](./06-CONVENTIONS.md)
