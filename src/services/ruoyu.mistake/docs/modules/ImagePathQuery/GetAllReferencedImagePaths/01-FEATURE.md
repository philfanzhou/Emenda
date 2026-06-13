# 图片路径查询 (GetAllReferencedImagePaths)

## 核心用户故事

作为外部系统（如存储清理服务），我希望查询错题系统中所有被引用的图片路径，以便安全地清理未被引用的存储对象。

## 补充约束

- 遍历所有 MistakeItem 的 SourceRegions.SourceImagePath
- 遍历所有 MistakePractice 的 ImagePaths
- 去重（大小写不敏感），过滤空值
- 返回 HashSet<string>，gRPC 层转为 repeated string
- 无参数，无分页

## 关键验收条件摘要

1. 返回所有 MistakeItem.SourceRegions 中的 SourceImagePath
2. 返回所有 MistakePractice.ImagePaths 中的路径
3. 相同路径（大小写不敏感）只出现一次
4. 空值和空白字符串被过滤
5. 无任何图片时返回空列表

## 范围外

- 不返回图片的引用来源信息（哪条错题/练习引用了该图片）
- 不校验图片是否实际存在于存储中
- 不支持增量查询或变更检测

## 文档索引

| 编号 | 文档 | 说明 |
|------|------|------|
| 01 | FEATURE.md | 功能概述 |
| 02 | SPEC.md | 详细规格 |
| 03 | DESIGN.md | 设计文档 |
| 04 | TASKS.md | 任务清单 |
| 05 | TESTS.md | 测试文档 |
| 06 | CONVENTIONS.md | 约定 |
