# 集成约定

## 数据库命名约定

### 表名

- 使用 snake_case：`mistake_item`、`mistake_practice`、`process_log`
- 实体类名使用 PascalCase + `Entity` 后缀：`MistakeItemEntity`、`MistakePracticeEntity`、`ProcessLogEntity`
- 通过 `[Table("...")]` 特性映射

### 列名

- 使用 snake_case：`student_id`、`review_status`、`mistake_item_id`
- 属性名使用 PascalCase：`StudentId`、`ReviewStatus`、`MistakeItemId`
- 通过 `[Column("...")]` 特性映射

### 主键

- 列名：`id`
- 类型：UUID
- 约束名：`PK_{表名}`，如 `PK_mistake_item`

### 外键

- 列名：`{关联表名单数}_id`，如 `mistake_item_id`
- 约束名：`FK_{子表}_{主表}_{外键列}`，如 `FK_mistake_practice_mistake_item_mistake_item_id`

### 索引

- 索引名：`IX_{表名}_{列名1}_{列名2}_...`，如 `IX_mistake_item_student_id_subject_grade_review_status`
- 使用 `CREATE INDEX IF NOT EXISTS` 确保幂等

## 迁移管理约定

### 迁移 ID

- 初始迁移 ID：`20260514051544_InitialCreate`
- 后续迁移遵循 EF Core 默认命名：`{timestamp}_{MigrationName}`

### 迁移标记（Stamp）

- 表已存在但 `__EFMigrationsHistory` 为空时，自动写入初始迁移记录
- 避免在已有数据库上重复执行迁移

### 旧表迁移

- 旧表迁移逻辑仅在 PostgreSQL 模式下执行
- 所有 DDL 操作使用 `IF EXISTS` / `IF NOT EXISTS` 确保幂等
- 迁移顺序：删除旧外键 → 重命名表 → 添加列 → 重命名列 → 删除列 → 创建索引 → 修正列名 → 补充外键

### SQLite 模式

- 不执行任何迁移逻辑
- 使用 `EnsureCreated` 确保表存在
- 不支持 `Migrate`，不支持 schema 变更

## JSONB 格式约定

### source_regions

- 存储类型：PostgreSQL `jsonb`，SQLite `text`
- 默认值：`'[]'`（空数组）
- 字段命名：camelCase（由 `JsonNamingPolicy.CamelCase` 控制）
- 结构：
  ```json
  [
    {
      "sourceImagePath": "string",
      "boundingBox": { "x1": 0, "y1": 0, "x2": 0, "y2": 0 }
    }
  ]
  ```
- `boundingBox` 可为 null

### image_paths

- 存储类型：PostgreSQL `jsonb`，SQLite `text`
- 默认值：`'[]'`（空数组）
- 字段命名：无命名策略（`JsonSerializerOptions = null`），字符串数组无需转换
- 结构：
  ```json
  ["path1", "path2"]
  ```

### ValueComparer 约定

- 所有 JSONB 集合属性必须配置自定义 `ValueComparer`
- 相等判断：使用 `SequenceEqual` 逐元素比较
- 哈希计算：聚合各元素哈希值
- 快照：通过序列化/反序列化或 `ToList()` 实现深/浅拷贝

### 数据库列类型声明

- PostgreSQL：`HasColumnType("jsonb")`
- SQLite：`HasColumnType("jsonb")` 被忽略，实际存储为 `text`
- `GetTableCreationSql` 中 SQLite 的 `image_paths` 列显式声明为 `text NOT NULL DEFAULT '[]'`
