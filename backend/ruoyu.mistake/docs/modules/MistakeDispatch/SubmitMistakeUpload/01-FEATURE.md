# 提交错题上传 (SubmitMistakeUpload)

## 核心用户故事

作为学生，我希望通过上传错题图片来提交错题记录，以便系统能够保存和管理我的错题信息供后续审核和练习使用。

## 补充约束

- 同一个 `source_upload_id` 可以多次提交，每次都会创建新的错题条目（不检查重复）
- 每个图片路径创建一个 `SourceRegionModel`，初始时 `BoundingBox` 为 null
- 创建的错题条目初始状态为 `PendingReview`

## 关键验收条件

- [x] 提交时验证 studentId 非空
- [x] 提交时验证 subject 在 1-9 范围内
- [x] 提交时验证 grade 在 1-12 范围内
- [x] 提交时验证 sourceUploadId 非空
- [x] 提交时验证 imagePaths 非空且至少包含一张图片
- [x] 每个 imagePath 创建一个 SourceRegionModel（BoundingBox=null）
- [x] 创建的 MistakeItemModel 的 ReviewStatus 为 PendingReview
- [x] 记录 ProcessLog（Action=Created, details="Mistake item created from upload"）
- [x] 返回创建的错题条目 ID 列表
- [x] 验证失败时抛出 MistakeValidationException，gRPC 层转换为 InvalidArgument

## 范围外

- 图片内容的 OCR 识别或分析
- 图片文件的实际存储（由上游上传服务负责）
- 对已有错题条目的去重检查
- 自动分类错题类型

## 文档索引

- [02-SPEC.md](./02-SPEC.md) - 功能规格与验收标准
- [03-DESIGN.md](./03-DESIGN.md) - 设计文档
- [04-TASKS.md](./04-TASKS.md) - 任务清单
- [05-TESTS.md](./05-TESTS.md) - 测试文档
- [06-CONVENTIONS.md](./06-CONVENTIONS.md) - 约定与规范
