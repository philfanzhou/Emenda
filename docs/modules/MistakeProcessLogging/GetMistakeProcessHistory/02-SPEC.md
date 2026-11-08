# 错题处理历史查询 - 详细规格

## 功能概述

查询指定错题条目的全部处理日志，按时间升序返回，用于展示错题从创建到掌握的完整生命周期。

## 用户故事

作为教师/学生，我希望查看某道错题的完整处理历史，以便追踪错题从录入到掌握的全过程。

## 功能要求

| ID | 要求 | 优先级 |
|----|------|--------|
| FR-01 | 接收 mistakeItemId (string)，解析为 Guid 后查询 ProcessLog | P0 |
| FR-02 | 通过 IProcessLogRepository.GetByMistakeItemIdAsync 获取日志列表 | P0 |
| FR-03 | 返回的日志按 CreatedAt 升序排列 | P0 |
| FR-04 | 日志的 Action 字段映射为 ProcessAction 枚举（gRPC 层） | P0 |
| FR-05 | 无日志时返回空列表，不报错 | P0 |
| FR-06 | 传入无效 Guid 格式时，gRPC 层抛出异常 | P1 |

## 验收标准

### AC-FR-01: 请求参数解析

- **Given** 客户端发送 GetMistakeProcessHistory 请求
- **When** request.Id 为有效 Guid 字符串
- **Then** 成功解析为 Guid 并传入领域服务

- **Given** 客户端发送 GetMistakeProcessHistory 请求
- **When** request.Id 不是有效 Guid 字符串
- **Then** gRPC 层抛出异常（Guid.Parse 失败）

### AC-FR-02/03: 查询与排序

- **Given** 数据库中存在 mistakeItemId 对应的 3 条 ProcessLog（CreatedAt 分别为 T1, T3, T2）
- **When** 调用 GetMistakeProcessHistory
- **Then** 返回 3 条日志，顺序为 T1 → T2 → T3

### AC-FR-05: 空结果

- **Given** 数据库中不存在 mistakeItemId 对应的 ProcessLog
- **When** 调用 GetMistakeProcessHistory
- **Then** 返回 ProcessLogListResponse，logs 列表为空

### AC-FR-04: 枚举映射

- **Given** ProcessLogModel.Action = 2（对应 ProcessAction.Analyzed）
- **When** 映射为 ProcessLogDto
- **Then** Action 字段为 ProcessAction.Analyzed (gRPC 枚举)

## 非功能需求

- 无特殊性能要求（简单查询，数据量有限）
- 无缓存要求

## 测试策略

| 层级 | 策略 |
|------|------|
| 领域服务 | 单元测试：Mock IProcessLogRepository，验证调用和返回值 |
| gRPC 服务 | 集成测试：验证 Guid 解析、枚举映射、空结果处理 |
| 仓储 | 集成测试：验证 OrderBy CreatedAt 升序 |
