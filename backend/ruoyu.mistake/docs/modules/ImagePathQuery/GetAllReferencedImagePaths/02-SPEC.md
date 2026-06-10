# 图片路径查询 - 详细规格

## 功能概述

查询错题系统中所有被引用的图片路径，供外部系统（如存储清理服务）判断哪些存储对象可安全删除。

## 用户故事

作为外部系统（如存储清理服务），我希望查询错题系统中所有被引用的图片路径，以便安全地清理未被引用的存储对象。

## 功能要求

| ID | 要求 | 优先级 |
|----|------|--------|
| FR-01 | 接收 Empty 请求（无参数） | P0 |
| FR-02 | 遍历所有 MistakeItem 的 SourceRegions.SourceImagePath，收集非空路径 | P0 |
| FR-03 | 遍历所有 MistakePractice 的 ImagePaths，收集非空路径 | P0 |
| FR-04 | 对路径去重（大小写不敏感比较） | P0 |
| FR-05 | 过滤空值和空白字符串 | P0 |
| FR-06 | 返回 GetAllReferencedImagePathsResponse（repeated string image_paths） | P0 |
| FR-07 | 无图片时返回空列表 | P0 |

## 验收标准

### AC-FR-02: MistakeItem 路径收集

- **Given** 存在 2 个 MistakeItem，各有 2 个 SourceRegion（路径分别为 "path1.jpg", "path2.jpg", "path3.jpg", "path4.jpg"）
- **When** 调用 GetAllReferencedImagePathsAsync
- **Then** 返回结果包含 "path1.jpg", "path2.jpg", "path3.jpg", "path4.jpg"

### AC-FR-03: MistakePractice 路径收集

- **Given** 存在 1 个 MistakePractice，ImagePaths = ["practice1.jpg", "practice2.jpg"]
- **When** 调用 GetAllReferencedImagePathsAsync
- **Then** 返回结果包含 "practice1.jpg", "practice2.jpg"

### AC-FR-04: 大小写不敏感去重

- **Given** MistakeItem 路径为 "Path1.jpg"，MistakePractice 路径为 "path1.JPG"
- **When** 调用 GetAllReferencedImagePathsAsync
- **Then** 返回结果中 "Path1.jpg" 和 "path1.JPG" 只保留一个（先加入者）

### AC-FR-05: 空值过滤

- **Given** 某 SourceRegion.SourceImagePath = "" 或 null，某 MistakePractice.ImagePaths 包含 "" 或 null
- **When** 调用 GetAllReferencedImagePathsAsync
- **Then** 空值和空白字符串不出现在结果中

### AC-FR-07: 无图片时返回空列表

- **Given** 无 MistakeItem 和 MistakePractice
- **When** 调用 GetAllReferencedImagePathsAsync
- **Then** 返回空 HashSet

## 非功能需求

- 性能：全表扫描，数据量大时可能较慢。当前设计适用于数据量有限的场景。
- 无缓存要求

## 测试策略

| 层级 | 策略 |
|------|------|
| 领域服务 | 单元测试：Mock 两个仓储，验证路径收集、去重、空值过滤 |
| gRPC 服务 | 集成测试：验证 Empty 请求和 repeated string 响应映射 |
