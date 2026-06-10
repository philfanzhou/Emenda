# 错题类型分析 - 详细规格

## 功能概述

对指定错题进行错误类型分析，返回类型编号、类型名称和分析理由。当前为硬编码占位实现，真正的分析逻辑待实现。

## 用户故事

作为教师，我希望系统能自动分析错题的错误类型，以便针对性地制定教学策略。

## 功能要求

| ID | 要求 | 优先级 |
|----|------|--------|
| FR-01 | 接收 AnalyzeMistakeRequest（mistake_item_id, content, correct_answer, student_answer），解析 mistake_item_id 为 Guid | P0 |
| FR-02 | 查询错题是否存在（GetByIdAsync） | P0 |
| FR-03 | 返回 MistakeTypeResponse（type, type_name, analysis_reason） | P0 |
| FR-04 | 错题存在时，记录 ProcessLog（Action=Analyzed, details="Mistake analyzed as {typeName}"） | P0 |
| FR-05 | 错题不存在时，仍返回分析结果但不记录 ProcessLog | P1 |
| FR-06 | [待实现] 真正的错题类型分析逻辑，利用 content/correct_answer/student_answer | P0 |
| FR-07 | [待实现] 定义完整的错误类型分类体系 | P0 |

## 验收标准

### AC-FR-01: 请求参数

- **Given** 客户端发送 AnalyzeMistakeRequest
- **When** mistake_item_id 为有效 Guid
- **Then** 成功解析并传入领域服务

- **Given** 客户端发送 AnalyzeMistakeRequest
- **When** mistake_item_id 不是有效 Guid
- **Then** gRPC 层抛出异常（Guid.Parse 失败）

### AC-FR-02/03: 硬编码返回值

- **Given** 任意 mistakeItemId
- **When** 调用 AnalyzeMistakeTypeAsync
- **Then** 返回 MistakeTypeResponse { Type=5, TypeName="其他错误", AnalysisReason="根据答案对比无法明确分类" }

### AC-FR-04: 错题存在时记录日志

- **Given** mistakeItemId 对应的错题存在
- **When** 调用 AnalyzeMistakeTypeAsync
- **Then** 记录 ProcessLog（Action=Analyzed, Details="Mistake analyzed as 其他错误"），并调用 SaveChangesAsync

### AC-FR-05: 错题不存在时不记录日志

- **Given** mistakeItemId 对应的错题不存在
- **When** 调用 AnalyzeMistakeTypeAsync
- **Then** 仍返回硬编码 MistakeTypeResponse，但不记录 ProcessLog，不调用 SaveChangesAsync

### AC-FR-06: Proto 参数未使用（已知限制）

- **Given** 客户端传入 content/correct_answer/student_answer
- **When** gRPC 层调用领域服务
- **Then** 这些参数被忽略，领域服务仅接收 mistakeItemId

## 非功能需求

- 当前无性能要求（硬编码返回）
- 真正实现时需考虑分析算法的响应时间

## 测试策略

| 层级 | 策略 |
|------|------|
| 领域服务 | 单元测试：Mock IMistakeItemRepository，验证硬编码返回值、ProcessLog 记录条件 |
| gRPC 服务 | 集成测试：验证 Guid 解析、参数传递 |
