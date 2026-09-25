# 集成任务清单

## 数据库连接配置

- [ ] 配置 `ConnectionStrings:Default` 连接字符串（PostgreSQL）
- [ ] 验证 PostgreSQL 连接（含 `Host=` 或 `Server=`）
- [ ] 确认数据库与 Quaestura（原 questionbank，已迁出）、student 服务物理隔离

## 表结构创建

- [ ] 创建 `mistake_item` 表及索引
- [ ] 创建 `mistake_practice` 表及索引和外键
- [ ] 创建 `process_log` 表及索引和外键
- [ ] 验证 JSONB 列默认值（`'[]'`）

## 旧表迁移（PostgreSQL）

- [ ] 检测旧表 `mistake` 是否存在
- [ ] 删除旧表外键约束
- [ ] 重命名 `mistake` → `mistake_item`
- [ ] 添加缺失列（`source_upload_id`, `source_regions`, `reviewer_id` 等）
- [ ] 重命名 `status` → `review_status`
- [ ] 删除废弃列（`date`, `image_paths`, `fingerprint`）
- [ ] 创建索引
- [ ] 修正 `process_log.mistake_id` → `mistake_item_id`
- [ ] 修正 `mistake_practice.mistake_id` → `mistake_item_id`
- [ ] 补充外键约束

## 迁移标记

- [ ] 检测表已存在但 `__EFMigrationsHistory` 为空的情况
- [ ] 自动创建 `__EFMigrationsHistory` 表
- [ ] 写入 `20260514051544_InitialCreate` 迁移记录

## EF Core 配置

- [ ] 注册 `MistakeDbContext` 到 DI 容器
- [ ] 配置 `source_regions` JSONB 列及 ValueComparer
- [ ] 配置 `image_paths` JSONB 列及 ValueComparer
- [ ] 配置级联删除（`mistake_practice`, `process_log`）
- [ ] 配置乐观并发（`updated_at` ConcurrencyCheck）

## 初始化验证

- [ ] PostgreSQL 模式：验证完整初始化流程
- [ ] 验证旧表迁移后数据完整性
- [ ] 验证级联删除行为
- [ ] 验证 JSONB 序列化/反序列化
