# Mistake 与 OSS 集成 — 任务清单

## 基础集成

- [ ] IOssService 接口定义（CopyObjectAsync, DeleteAsync）
- [ ] S3OssService 实现（MinIO/SeaweedFS 兼容）
- [ ] LocalFileOssService 实现（本地文件系统）
- [ ] 依赖注入注册（Program.cs 配置切换逻辑）

## 图片迁移

- [ ] CompleteUploadReview 中实现 confirmed item 的图片迁移
- [ ] 路径映射逻辑（uploads/ → mistakes/ 前缀替换）
- [ ] pathMapping 缓存避免重复迁移
- [ ] 单个图片迁移失败的容错处理
- [ ] 迁移后删除源对象（uploads/ 路径）

## 图片引用查询

- [ ] GetAllReferencedImagePaths 实现
- [ ] 收集 mistake_item.source_regions 中的路径
- [ ] 收集 mistake_practice.image_paths 中的路径
- [ ] 去重并返回路径集合

## OSS 审计

- [ ] OssAuditWorker 实现
- [ ] 调用 Mistake.GetAllReferencedImagePaths
- [ ] 调用 Student.GetRegisteredOssPaths
- [ ] 交叉比对计算僵尸对象
- [ ] 僵尸对象清理策略

## 配置

- [ ] 生产模式配置项（Oss:Endpoint, AccessKey, SecretKey, BucketName）
- [ ] 开发模式配置项（USE_LOCAL_OSS, OSS_LOCAL_PATH）
- [ ] Program.cs 中的模式切换逻辑

## 测试

- [ ] 正常迁移图片测试
- [ ] 单个图片迁移失败测试
- [ ] OSS 不可用场景测试
- [ ] 本地 OSS 模式测试
- [ ] GetAllReferencedImagePaths 正常场景测试
- [ ] GetAllReferencedImagePaths 大数据量性能测试
- [ ] OssAuditWorker 审计流程测试
