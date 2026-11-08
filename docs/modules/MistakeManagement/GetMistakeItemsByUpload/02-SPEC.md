# 按上传记录查询错题 — 详细需求规格 (SPEC)

## 功能概述

根据上传记录 ID（sourceUploadId）查询该上传下的所有错题条目，返回完整列表。用于审核流程中查看某次上传的整体情况。

## 功能要求清单

- [ ] FR-01: 响应 `GetMistakeItemsByUpload` gRPC 请求，返回 `MistakeItemListResponse`
- [ ] FR-02: 按 sourceUploadId 查询所有关联的错题条目
- [ ] FR-03: 无错题时返回空列表（非错误）
- [ ] FR-04: 结果按 `created_at` 升序排列

## 验收标准

### AC-FR-02: 有错题时返回列表

- **Given**: sourceUploadId="upload-1" 下有 3 条错题
- **When**: 调用 `GetMistakeItemsByUpload(sourceUploadId="upload-1")`
- **Then**: 返回 3 条 `MistakeItemDto`，按 created_at 升序排列

### AC-FR-03: 无错题时返回空列表

- **Given**: sourceUploadId="upload-empty" 下无错题
- **When**: 调用 `GetMistakeItemsByUpload(sourceUploadId="upload-empty")`
- **Then**: 返回 `MistakeItemListResponse`，`items` 为空列表

## 非功能需求

- **性能**: 查询使用 `source_upload_id` 索引，单次查询不应超过 50ms
- **可靠性**: 数据库不可用时返回 gRPC Internal 错误

## 测试策略

- 单元测试覆盖 Domain Service 的列表查询逻辑
- 集成测试验证客户端排序的正确性
