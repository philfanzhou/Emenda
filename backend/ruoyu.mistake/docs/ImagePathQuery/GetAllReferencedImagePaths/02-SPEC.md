# GetAllReferencedImagePaths — 详细需求规格 (SPEC)

## 功能概述和用户故事

**概述**：所有引用图片路径查询通过 gRPC 接口（无参数）查询 ruoyu.mistake 服务中所有已被数据库引用的 OSS 图片路径，收集自 `MistakeItem` 的 `SourceRegions.SourceImagePath` 和 `MistakePractice` 的 `ImagePaths`，经 `HashSet` 去重后返回，用于 OSS 垃圾回收。

**用户故事 1（运维人员查询引用路径）**：作为运维人员，我要调用 GetAllReferencedImagePaths 获取所有已引用路径列表；系统从数据库中收集全部路径、去重后返回，用于与 OSS 对象列表做对比。

## 功能要求清单（可独立测试）

- [x] FR-01 能查询所有 `MistakeItem` 记录的 `SourceRegions` 中的图片路径。
- [x] FR-02 能查询所有 `MistakePractice` 记录的 `ImagePaths` 中的图片路径。
- [x] FR-03 返回的路径列表经去重处理，无重复项。
- [x] FR-04 返回的路径列表中不包含空字符串或空白字符串。
- [x] FR-05 接口无需任何参数。
- [x] FR-06 返回类型为 `repeated string image_paths`。

## 详细的验收标准（可自动验证）

- AC-FR-01：调用接口后，返回的 `GetAllReferencedImagePathsResponse.ImagePaths` 包含所有 `MistakeItem.SourceRegions` 中的非空白 `SourceImagePath`。
- AC-FR-02：调用接口后，返回的 `GetAllReferencedImagePathsResponse.ImagePaths` 包含所有 `MistakePractice.ImagePaths` 中的非空白路径。
- AC-FR-03：若同一路径同时出现在 `MistakeItem` 和 `MistakePractice` 中，结果中仅出现一次（去重）。
- AC-FR-04：调用接口后，`_mistakeItemRepository.GetAllAsync` 被调用一次，`_mistakePracticeRepository.GetAllAsync` 被调用一次。
- AC-FR-05：去重使用大小写不敏感比较（`StringComparer.OrdinalIgnoreCase`）。

## 非功能需求

- **性能**：查询操作 P95 < 500ms；数据量大时应考虑全量加载的内存开销。
- **安全**：仅管理员/运维人员可调用此接口（由上层 API 网关负责鉴权）。
- **可靠性**：查询为只读操作，不修改任何数据；部分数据加载失败不影响整体流程。
- **可观测性**：不记录日志（纯查询操作）。

## 测试策略

- **覆盖率目标**：`MistakeDomainService.GetAllReferencedImagePathsAsync` 代码行覆盖率 ≥ 80%。
- **必须测试的错误路径**：仓储返回空列表、部分路径为空字符串。
- **测试环境要求**：使用 `xUnit + Moq` 进行单元测试；所有外部依赖（仓储）均可替换。
- **禁止事项**：不得写入真实生产数据库。