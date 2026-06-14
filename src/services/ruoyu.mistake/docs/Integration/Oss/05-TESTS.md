# Mistake 与 OSS 集成 — 测试用例

## 图片迁移

### TC-MIG-001 正常迁移图片

**前置条件**：
- OSS 服务可用
- uploads/ 路径下存在待迁移图片

**步骤**：
1. 调用 CompleteUploadReview，传入 confirmed items
2. 验证每个图片被复制到 mistakes/ 路径
3. 验证原始 uploads/ 路径的图片已删除
4. 验证 mistake_item 中的路径已更新为 mistakes/ 前缀

**预期结果**：
- 所有图片成功迁移至 mistakes/ 路径
- uploads/ 路径下无残留
- pathMapping 缓存记录了所有迁移映射

### TC-MIG-002 路径缓存避免重复迁移

**前置条件**：
- 同一图片路径被多个 confirmed item 引用

**步骤**：
1. 调用 CompleteUploadReview，传入包含重复图片路径的 items
2. 验证同一路径只执行一次 CopyObjectAsync 和 DeleteAsync

**预期结果**：
- CopyObjectAsync 对同一路径仅调用一次
- pathMapping 缓存命中后跳过迁移

### TC-MIG-003 单个图片迁移失败

**前置条件**：
- OSS 服务可用
- 某个图片的 CopyObjectAsync 抛出异常

**步骤**：
1. 调用 CompleteUploadReview，传入多个 confirmed items
2. 其中一个图片迁移失败
3. 验证其他图片正常迁移
4. 验证审核完成状态正常保存

**预期结果**：
- 失败图片不影响其他图片迁移
- 失败图片的路径仍指向 uploads/ 前缀
- 审核完成状态已保存
- 日志中记录了迁移失败信息

### TC-MIG-004 OSS 不可用

**前置条件**：
- OSS 服务完全不可用（所有调用均抛出异常）

**步骤**：
1. 调用 CompleteUploadReview，传入 confirmed items
2. 所有图片迁移均失败
3. 验证审核完成状态仍正常保存

**预期结果**：
- 所有图片路径仍指向 uploads/ 前缀
- 审核完成状态已保存，未回滚
- 日志中记录了所有迁移失败信息

### TC-MIG-005 本地 OSS 模式

**前置条件**：
- USE_LOCAL_OSS=1
- OSS_LOCAL_PATH 指向本地目录

**步骤**：
1. 使用 LocalFileOssService 执行图片迁移
2. 验证文件被复制到本地 mistakes/ 目录
3. 验证原始 uploads/ 文件已删除

**预期结果**：
- 行为与 S3OssService 一致
- 文件操作在本地文件系统完成

### TC-MIG-006 Rejected item 不迁移

**前置条件**：
- 存在 rejected items

**步骤**：
1. 调用 CompleteUploadReview，传入 rejected items
2. 验证 rejected items 的图片未被迁移
3. 验证 uploads/ 路径下原图仍存在

**预期结果**：
- Rejected items 的图片保留在 uploads/ 路径
- 无 CopyObjectAsync / DeleteAsync 调用

## 图片引用查询

### TC-REF-001 正常返回引用路径

**前置条件**：
- 数据库中存在 mistake_item 和 mistake_practice 数据

**步骤**：
1. 调用 GetAllReferencedImagePaths
2. 验证返回路径包含 source_regions 和 image_paths 中的所有路径

**预期结果**：
- 返回的路径集合覆盖所有引用
- 无重复路径

### TC-REF-002 大数据量场景

**前置条件**：
- 数据库中存在大量 mistake_item 和 mistake_practice 记录

**步骤**：
1. 调用 GetAllReferencedImagePaths
2. 监控内存使用和响应时间

**预期结果**：
- 功能正常返回
- 记录性能指标，评估是否需要分页支持

## OSS 审计

### TC-AUD-001 僵尸对象检测

**前置条件**：
- OSS 中存在未被任何模块引用的对象

**步骤**：
1. OssAuditWorker 执行审计
2. 获取 Mistake 和 Student 的引用路径
3. 与 OSS 全量对象交叉比对

**预期结果**：
- 正确识别出僵尸对象
- 僵尸对象不在 Mistake 和 Student 的引用路径中

## 图片迁移路径计算 UT

测试文件：`test/Ruoyu.Study.Mistake.Domain.Tests/ImagePathMigrationTests.cs`、`test/Ruoyu.Study.Mistake.Service.Tests/MistakeServiceImplOssMigrationTests.cs`

### TC-PATH-01 路径替换 — uploads/ → mistakes/ 前缀替换

- **Given**：`SourceRegion.SourceImagePath = "uploads/2025/06/abc.jpg"`
- **When**：执行路径迁移计算
- **Then**：新路径为 `"mistakes/2025/06/abc.jpg"`（仅替换前缀 `uploads` → `mistakes`，路径其余部分保持不变）

### TC-PATH-02 路径替换 — 含子目录结构

- **Given**：`SourceRegion.SourceImagePath = "uploads/student-1/2026/05/03/record/upload_0.jpg"`
- **When**：执行路径迁移计算
- **Then**：新路径为 `"mistakes/student-1/2026/05/03/record/upload_0.jpg"`

### TC-PATH-03 路径去重 — 同一路径被多个 confirmed item 引用

- **Given**：两个 confirmed item 的 SourceRegion 引用相同路径 `"uploads/student-1/img1.jpg"`
- **When**：调用 CompleteUploadReview
- **Then**：`MigrateImagesToMistakeAsync` 仅被调用一次（pathsToMigrate 列表去重），两个 item 的路径均更新为 `"mistakes/student-1/img1.jpg"`

### TC-PATH-04 路径去重 — 大小写不敏感

- **Given**：一个 item 引用 `"uploads/Student-1/img1.jpg"`，另一个引用 `"uploads/student-1/img1.jpg"`
- **When**：执行路径迁移
- **Then**：路径按大小写敏感处理（OSS 路径区分大小写），两条路径均被迁移

### TC-PATH-05 特殊字符处理 — 路径含空格

- **Given**：`SourceRegion.SourceImagePath = "uploads/2025/06/my image.jpg"`
- **When**：执行路径迁移计算
- **Then**：新路径为 `"mistakes/2025/06/my image.jpg"`（空格保留，不做编码）

### TC-PATH-06 特殊字符处理 — 路径含中文字符

- **Given**：`SourceRegion.SourceImagePath = "uploads/2025/06/错题图片.jpg"`
- **When**：执行路径迁移计算
- **Then**：新路径为 `"mistakes/2025/06/错题图片.jpg"`（中文字符保留）

### TC-PATH-07 特殊字符处理 — 路径含 URL 编码字符

- **Given**：`SourceRegion.SourceImagePath = "uploads/2025/06/image%20test.jpg"`
- **When**：执行路径迁移计算
- **Then**：新路径为 `"mistakes/2025/06/image%20test.jpg"`（编码字符原样保留）

### TC-PATH-08 非 uploads 前缀路径不迁移

- **Given**：`SourceRegion.SourceImagePath = "mistakes/2025/06/already_migrated.jpg"`（已迁移路径）
- **When**：执行路径迁移
- **Then**：该路径不被加入 pathsToMigrate 列表，不触发 CopyObjectAsync

### TC-PATH-09 空路径和空白路径跳过

- **Given**：`SourceRegion.SourceImagePath = ""` 或 `"   "` 或 `null`
- **When**：执行路径迁移
- **Then**：该路径被跳过，不触发任何 OSS 操作
