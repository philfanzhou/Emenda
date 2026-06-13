# 数据库与存储层集成

## 功能概述

Mistake 服务提供错题管理的数据库与存储层集成，支持 PostgreSQL 和 SQLite 双模式，具备自动迁移、旧表迁移和完整的初始化能力。

## 核心能力

### 1. PostgreSQL / SQLite 双模式

- **PostgreSQL**：当 `ConnectionStrings:Default` 包含 `Host=` 或 `Server=` 时自动启用，使用 Npgsql 提供程序
- **SQLite**：当连接字符串不含上述关键字时启用，默认路径 `Data Source=data/sqlite/ruoyu_study_mistake.db`
- 数据库名：`ruoyu_study_mistake`，与 `ruoyu_study_questionbank` 和 `ruoyu_study_student` 物理隔离

### 2. 自动迁移和初始化

- **PostgreSQL 模式**：执行 EF Core Migrate + 旧表迁移 + 迁移标记补全 + 列名修正 + 外键补全
- **SQLite 模式**：跳过迁移逻辑，直接使用 `EnsureCreated` 确保表存在

### 3. 旧表迁移支持

- 检测旧表 `mistake`，自动迁移到 `mistake_item`
- 处理列重命名（`status` → `review_status`，`mistake_id` → `mistake_item_id`）
- 补充新增列（`source_upload_id`、`source_regions`、`reviewer_id` 等）
- 删除废弃列（`date`、`image_paths`、`fingerprint`）
- 自动重建索引和外键约束

### 4. JSONB 存储

- `source_regions`：存储错题来源区域信息（PostgreSQL JSONB / SQLite text）
- `image_paths`：存储练习图片路径列表（PostgreSQL JSONB / SQLite text）
- 自定义 `ValueComparer` 确保变更追踪正确

### 5. 级联删除

- `mistake_practice` 和 `process_log` 的外键均配置 `ON DELETE CASCADE`
- 删除 `mistake_item` 时自动清理关联的练习记录和流程日志

### 6. 乐观并发

- `mistake_item.updated_at` 字段标记 `[ConcurrencyCheck]`
- 并发更新时自动检测冲突
