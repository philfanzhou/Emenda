# 迁移历史

## 20260514051544_InitialCreate

**日期**: 2026-05-14  
**类型**: 初始创建  
**文件**: [Migrations/20260514051544_InitialCreate.cs](file://src/Database/Migrations/20260514051544_InitialCreate.cs)

### 创建的表

1. **mistake_item** — 包含所有字段及 5 个索引
2. **mistake_practice** — 包含外键 CASCADE 约束及 3 个索引
3. **process_log** — 包含外键 CASCADE 约束及 2 个索引

### 旧表迁移（DatabaseInitializer）

`DatabaseInitializer.MigrateOldSchemaAsync` 处理从旧表 `mistake` 到 `mistake_item` 的迁移：

1. 检测旧表 `mistake` 是否存在
2. 删除旧表的外键约束
3. 如果 `mistake_item` 已存在：直接删除旧表
4. 如果 `mistake_item` 不存在：重命名表 + 添加新列 + 移除旧列 + 创建新索引
5. 修复 `process_log` 和 `mistake_practice` 的列名 `mistake_id` → `mistake_item_id` 及外键约束

### 已移除的列（旧表）

| 列名 | 说明 |
|------|------|
| `date` | 旧版本日期字段，已被 `created_at` 替代 |
| `image_paths` | 旧版本图片路径，已被 `source_regions` (JSONB) 替代 |
| `fingerprint` | 图片指纹字段，已移除 |