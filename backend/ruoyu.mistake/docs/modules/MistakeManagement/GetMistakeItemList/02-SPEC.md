# 错题列表查询 — 详细需求规格 (SPEC)

## 功能概述

提供分页查询错题列表的能力，支持按学生ID、学科、年级、审核状态筛选。这是错题管理模块的列表入口。

## 功能要求清单

- [ ] FR-01: 响应 `GetMistakeItemList` gRPC 请求，返回 `MistakeItemPageResult`
- [ ] FR-02: 必传 `studentId` 参数，按学生ID过滤
- [ ] FR-03: 可选 `subject` 参数（>0 时筛选）
- [ ] FR-04: 可选 `grade` 参数（>0 时筛选）
- [ ] FR-05: 可选 `reviewStatus` 参数（>0 时筛选）
- [ ] FR-06: 参数校验：page<=0→1, size<=0→10, size>100→100
- [ ] FR-07: 返回 `PageMeta` 包含 page/size/totalCount/totalPages
- [ ] FR-08: 结果按 `created_at` 降序排列

## 验收标准

### AC-FR-01: 完整查询

- **Given**: 存在 studentId="s1" 的错题 15 条，subject=1 的 5 条
- **When**: 调用 `GetMistakeItemList(studentId="s1", subject=1, page=1, size=10)`
- **Then**: 返回 5 条结果，totalCount=5，totalPages=1

### AC-FR-02: 分页第二页

- **Given**: 存在 studentId="s1" 的错题 25 条
- **When**: 调用 `GetMistakeItemList(studentId="s1", page=2, size=10)`
- **Then**: 返回 10 条结果，totalCount=25，totalPages=3

### AC-FR-03: page 参数修正

- **Given**: 任意数据
- **When**: 调用 `GetMistakeItemList(studentId="s1", page=0, size=10)`
- **Then**: page 被修正为 1，正常返回结果

### AC-FR-04: size 参数修正

- **Given**: 任意数据
- **When**: 调用 `GetMistakeItemList(studentId="s1", page=1, size=200)`
- **Then**: size 被修正为 100

## 非功能需求

- **性能**: 查询使用复合索引 `(student_id, subject, grade, review_status)`，单次查询不应超过 100ms
- **可靠性**: 数据库不可用时返回 gRPC Internal 错误

## 测试策略

- 单元测试覆盖参数校验和分页修正逻辑
- 集成测试验证数据库查询正确性