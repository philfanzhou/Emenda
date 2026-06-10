# GetMistakePractices - 功能规格

## 功能概述

通过 gRPC 接口查询指定错题的所有练习记录，结果按练习时间升序排列，无分页。

## 用户故事

作为教师或学生，我希望能够查看某条错题的所有练习记录，以便了解该错题的练习情况和掌握进度。

## 功能要求

### FR-01 根据错题ID查询练习记录

系统接受 `IdRequest`（包含错题 ID），返回该错题的所有练习记录。

### FR-02 按练习时间升序排列

查询结果按 `PracticeDate` 升序排列，最早的练习记录排在前面。

### FR-03 无分页返回全部

系统返回该错题的全部练习记录，不提供分页功能。

### FR-04 空结果处理

当错题不存在或没有练习记录时，返回空列表（非错误）。

## 验收标准

### AC-FR-01

**Given** 错题 ID "abc" 关联了 3 条练习记录
**When** 调用 GetMistakePractices RPC，传入 id="abc"
**Then** 返回 `MistakePracticeListResponse`，practices 数量为 3

### AC-FR-02

**Given** 错题 ID "abc" 关联了多条练习记录，PracticeDate 分别为 T1、T2、T3（T1 < T2 < T3）
**When** 调用 GetMistakePractices RPC
**Then** 返回的 practices 按 PracticeDate 升序排列：T1、T2、T3

### AC-FR-03

**Given** 错题 ID "abc" 没有关联的练习记录
**When** 调用 GetMistakePractices RPC
**Then** 返回 `MistakePracticeListResponse`，practices 为空列表

### AC-FR-04

**Given** 不存在的错题 ID
**When** 调用 GetMistakePractices RPC
**Then** 返回 `MistakePracticeListResponse`，practices 为空列表

## 非功能需求

- 查询操作应在 200ms 内完成
- 当前无分页，若单条错题练习记录超过 100 条建议增加分页支持

## 测试策略

- 单元测试：验证领域服务 `GetPracticesAsync` 的查询和排序逻辑
- 集成测试：验证 gRPC 层的请求映射和响应格式
