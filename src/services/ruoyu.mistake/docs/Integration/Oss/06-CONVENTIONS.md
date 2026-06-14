# Mistake 与 OSS 集成 — 约定

## OSS 路径命名约定

### 前缀规则

| 用途 | 前缀 | 说明 |
|------|------|------|
| 临时上传 | `uploads/` | 上传审核阶段的图片存放路径 |
| 正式存储 | `mistakes/` | 审核通过后的图片存放路径 |

### 路径结构

```
{prefix}/{subject}/{student_id}/{year}/{month}/{day}/{mistake_id}/{filename}
```

- `subject`：学科标识
- `student_id`：学生 ID
- `year}/{month}/{day}`：日期分段
- `mistake_id`：错题 ID
- `filename`：文件名

### 文件名规则

| 类型 | 格式 | 示例 |
|------|------|------|
| 错题原图 | `original_{index}.jpg` | `original_0.jpg` |
| 练习图片 | `practice_{practice_id}_{index}.jpg` | `practice_p1_0.jpg` |

## 迁移语义约定

1. **仅前缀替换**：迁移时仅将 `uploads` 替换为 `mistakes`，路径其余部分保持不变
2. **先复制后删除**：迁移顺序为 CopyObjectAsync → DeleteAsync，确保数据安全
3. **路径缓存**：同一审核批次内，已迁移的路径通过 `pathMapping` 缓存，避免重复操作
4. **幂等性**：对同一源路径重复执行迁移不应产生副作用（目标已存在则覆盖，源已删除则忽略）

## 错误处理约定

### 迁移失败

- 单个图片迁移失败**不阻塞**整体流程
- 失败时记录日志，包含源路径和异常信息
- 失败的图片路径**不更新**，仍指向原始 `uploads/` 前缀
- 审核完成状态**不回滚**

### OSS 不可用

- 所有迁移操作失败时，审核完成状态仍正常保存
- 业务逻辑与 OSS 操作解耦，OSS 故障不影响核心业务状态
- 需提供修复机制，在 OSS 恢复后可重新触发迁移

### 审计查询

- `GetAllReferencedImagePaths` 返回全量数据，无分页
- 调用方需评估数据量对内存和性能的影响
- 未来如数据量增长，需考虑分页或流式返回

## GetPresignedUrl 约定（oss-path-autonomy）

### 直连 OSS

- Mistake 恢复直连 OSS，`GetPresignedUrl` 直接调用 `IOssService` + `ThumbnailHelper`
- 不再代理到 Student gRPC `GetPresignedUrl`
- 路径前缀校验仅允许 `mistakes/`

### 缩略图路径约定

- 缩略图与原图同目录，路径格式 `{dirname}/{stem}_{size}.jpg`
- 示例：原图 `mistakes/2025/06/14/def/image.jpg` → 缩略图 `mistakes/2025/06/14/def/image_small.jpg`
- `IOssService.DeleteAsync` 自动清理关联缩略图，无需手动处理

## 配置约定

- 生产环境必须配置 `Oss:Endpoint`、`Oss:AccessKey`、`Oss:SecretKey`、`Oss:BucketName`
- 开发环境设置 `USE_LOCAL_OSS=1` 启用本地模式，`OSS_LOCAL_PATH` 可选（默认 `data/oss`）
- 两种模式通过 `Program.cs` 中的条件逻辑切换，不共存
