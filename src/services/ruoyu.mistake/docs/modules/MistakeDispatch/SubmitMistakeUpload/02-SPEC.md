# SubmitMistakeUpload - 功能规格

## 功能概述

学生通过上传错题图片提交错题记录。系统为每次提交创建一个 `MistakeItemModel`，每张图片对应一个 `SourceRegionModel`，初始状态为待审核。

## 用户故事

作为学生，我希望通过上传错题图片来提交错题记录，以便系统能够保存和管理我的错题信息供后续审核和练习使用。

## 功能要求

- [x] FR-01: 接收学生提交的错题上传请求，包含学生ID、学科、年级、图片路径列表、备注和上传记录ID
- [x] FR-02: 验证必填字段（studentId、sourceUploadId、imagePaths）和范围字段（subject 1-9、grade 1-12）
- [x] FR-03: 为每个图片路径创建 SourceRegionModel（BoundingBox=null）
- [x] FR-04: 创建 MistakeItemModel 并设置 ReviewStatus=PendingReview
- [x] FR-05: 记录 ProcessLog（Action=Created）
- [x] FR-06: 返回创建的错题条目 ID 列表
- [x] FR-07: 验证失败时返回明确的错误信息

## 验收标准

### AC-FR-01: 接收错题上传请求

- **Given** 有效的学生ID、学科(1-9)、年级(1-12)、至少一张图片路径和上传记录ID
- **When** 调用 SubmitMistakeUpload
- **Then** 成功创建一个 MistakeItemModel 并返回其 ID

### AC-FR-02: 参数验证 - studentId

- **Given** studentId 为空或空白
- **When** 调用 SubmitMistakeUpload
- **Then** 抛出 MistakeValidationException，gRPC 层返回 InvalidArgument，错误信息为"缺少学生ID"

### AC-FR-02b: 参数验证 - subject

- **Given** subject 不在 1-9 范围内
- **When** 调用 SubmitMistakeUpload
- **Then** 抛出 MistakeValidationException，gRPC 层返回 InvalidArgument，错误信息为"学科值无效（有效范围：1-9）"

### AC-FR-02c: 参数验证 - grade

- **Given** grade 不在 1-12 范围内
- **When** 调用 SubmitMistakeUpload
- **Then** 抛出 MistakeValidationException，gRPC 层返回 InvalidArgument，错误信息为"年级值无效（有效范围：1-12）"

### AC-FR-02d: 参数验证 - sourceUploadId

- **Given** sourceUploadId 为空或空白
- **When** 调用 SubmitMistakeUpload
- **Then** 抛出 MistakeValidationException，gRPC 层返回 InvalidArgument，错误信息为"缺少上传记录ID"

### AC-FR-02e: 参数验证 - imagePaths

- **Given** imagePaths 为空列表
- **When** 调用 SubmitMistakeUpload
- **Then** 抛出 MistakeValidationException，gRPC 层返回 InvalidArgument，错误信息为"至少需要提供一张图片"

### AC-FR-03: SourceRegion 创建

- **Given** 提交了 N 张图片路径
- **When** 创建 MistakeItemModel
- **Then** SourceRegions 包含 N 个 SourceRegionModel，每个的 SourceImagePath 对应输入路径，BoundingBox 为 null

### AC-FR-04: 初始状态

- **Given** 成功创建错题条目
- **When** 查看创建的 MistakeItemModel
- **Then** ReviewStatus 为 PendingReview(1)，CreatedAt 为当前 UTC 时间

### AC-FR-05: ProcessLog 记录

- **Given** 成功创建错题条目
- **When** 查看 ProcessLog
- **Then** 存在一条记录，Action=Created(1)，details="Mistake item created from upload"

### AC-FR-06: 重复提交

- **Given** 同一个 sourceUploadId 已有错题条目
- **When** 再次调用 SubmitMistakeUpload
- **Then** 创建新的错题条目（不检查重复），返回新的 ID

### AC-FR-08: UploadPollingWorker 幂等检查

- **Given** UploadPollingWorker 轮询到一条 Processing 记录，该 uploadId 下已有 MistakeItem
- **When** Worker 执行幂等检查
- **Then** 从已有 MistakeItem 的 SourceRegions 中提取已处理图片路径，计算未处理图片列表，只对未处理图片执行 VL 分析和 MistakeItem 创建

### AC-FR-09: 全部已处理时跳过分析

- **Given** UploadPollingWorker 轮询到一条 Processing 记录，该 uploadId 下所有图片均已有对应 MistakeItem
- **When** Worker 执行幂等检查
- **Then** 跳过 VL 分析和 OSS 迁移，直接通知 Student 删除全部已处理图片

## 非功能需求

- NFR-01: 验证错误信息需明确指出具体字段和有效范围
- NFR-02: gRPC 层和领域层均执行验证，双重保障数据完整性
- NFR-03: 多个验证错误时合并为一条消息返回（领域层用分号分隔）

## 测试策略

- 单元测试覆盖领域服务 `MistakeDomainService.SubmitMistakeUploadAsync`
- 验证正常流程和重复提交场景
- 验证各参数的边界条件
- gRPC 层验证由集成测试覆盖
