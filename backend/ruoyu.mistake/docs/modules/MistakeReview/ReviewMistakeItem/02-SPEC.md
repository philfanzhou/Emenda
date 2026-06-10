# ReviewMistakeItem - 功能规格

## 功能概述

教师对学生提交的待审核错题进行审核，确认有效错题或驳回无效错题。确认审核时自动迁移图片存储路径。

## 用户故事

作为教师，我希望能够审核学生提交的错题条目（确认或驳回），以便确认有效的错题进入练习流程，驳回无效的错题要求重新上传。

## 功能要求

- [x] FR-01: 接收审核请求，包含错题ID、审核状态、审核人ID、审核评论及可选的年级/学科更新
- [x] FR-02: 验证审核状态只能是 Confirmed 或 Rejected
- [x] FR-03: 验证只能审核 PendingReview 状态的错题
- [x] FR-04: 更新错题的 ReviewStatus、ReviewerId、ReviewedAt、ReviewComment、UpdatedAt
- [x] FR-05: 可选更新 grade（1-12）和 subject（1-9）
- [x] FR-06: 记录 ProcessLog（Action=ReviewConfirmed 或 ReviewRejected）
- [x] FR-07: Confirmed 时迁移图片：uploads/ → mistakes/（OSS Copy+Delete）
- [x] FR-08: 收集所有 SourceImagePath 到 removed_image_paths 返回
- [x] FR-09: 迁移后更新 MistakeItemModel 的 SourceImagePath 并持久化

## 验收标准

### AC-FR-01: 审核请求

- **Given** 有效的错题ID、审核状态(Confirmed/Rejected)、审核人ID
- **When** 调用 ReviewMistakeItem
- **Then** 更新错题条目的审核信息并返回成功响应

### AC-FR-02: 审核状态验证

- **Given** 审核状态不是 Confirmed 也不是 Rejected
- **When** 调用 ReviewMistakeItem
- **Then** gRPC 层直接返回 InvalidArgument，错误信息为"审核结果只能是确认或驳回"

### AC-FR-03: 只能审核待审核状态

- **Given** 错题条目的 ReviewStatus 不是 PendingReview（如已 Confirmed 或 Rejected）
- **When** 调用 ReviewMistakeItem
- **Then** 抛出 MistakeValidationException，错误信息为"只能审核待审核状态的错题"

### AC-FR-04: 更新审核信息

- **Given** 错题条目为 PendingReview 状态，审核结果为 Confirmed
- **When** 调用 ReviewMistakeItemAsync
- **Then** ReviewStatus 更新为 Confirmed，ReviewerId 和 ReviewComment 被设置，ReviewedAt 和 UpdatedAt 为当前 UTC 时间

### AC-FR-05: 可选更新 grade 和 subject

- **Given** 审核请求中 grade > 0 且 <= 12，subject > 0 且 <= 9
- **When** 调用 ReviewMistakeItemAsync
- **Then** 错题条目的 Grade 和 Subject 被更新

- **Given** 审核请求中 grade <= 0 或 subject <= 0
- **When** 调用 ReviewMistakeItemAsync
- **Then** Grade 和 Subject 不被更新

### AC-FR-06: ProcessLog 记录

- **Given** 审核结果为 Confirmed
- **When** 审核完成
- **Then** ProcessLog 记录 Action=ReviewConfirmed(3)，details="Mistake item confirmed"

- **Given** 审核结果为 Rejected
- **When** 审核完成
- **Then** ProcessLog 记录 Action=ReviewRejected(4)，details="Mistake item rejected"

### AC-FR-07: 图片迁移（Confirmed 时）

- **Given** 审核结果为 Confirmed，且 SourceRegion 的 SourceImagePath 以 "uploads/" 开头
- **When** gRPC 层处理审核结果
- **Then** 执行 OSS Copy（uploads/xxx → mistakes/xxx）和 Delete（uploads/xxx），更新 SourceImagePath 为新路径

- **Given** 审核结果为 Confirmed，且 SourceImagePath 不以 "uploads/" 开头
- **When** gRPC 层处理审核结果
- **Then** 不执行迁移，SourceImagePath 不变

### AC-FR-08: removed_image_paths

- **Given** 审核结果为 Confirmed，错题有 N 个 SourceRegion
- **When** gRPC 层处理审核结果
- **Then** removed_image_paths 包含所有 SourceImagePath（包括未迁移的）

### AC-FR-09: 错题不存在

- **Given** 指定 ID 的错题条目不存在
- **When** 调用 ReviewMistakeItemAsync
- **Then** 返回 null，gRPC 层返回 success=false，error_message="错题不存在"

## 非功能需求

- NFR-01: 图片迁移操作需保证原子性（Copy 成功后才 Delete）
- NFR-02: 迁移失败不应影响审核状态的更新
- NFR-03: OSS 操作失败需记录错误日志

## 测试策略

- 单元测试覆盖领域服务 `MistakeDomainService.ReviewMistakeItemAsync`
- 覆盖确认、驳回、重复审核三种场景
- gRPC 层图片迁移逻辑由集成测试覆盖
