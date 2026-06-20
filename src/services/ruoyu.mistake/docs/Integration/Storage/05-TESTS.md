# 测试用例

## PostgreSQL 初始化

### TC-PG-01: 全新数据库初始化

- **前置条件**：空数据库，`ConnectionStrings:Default` 包含 `Host=`
- **步骤**：启动服务，调用 `InitializeAsync`
- **预期**：
  - 创建 `mistake_item`、`mistake_practice`、`process_log` 三张表
  - 创建所有索引
  - 创建外键约束
  - `__EFMigrationsHistory` 包含 `20260514051544_InitialCreate` 记录

### TC-PG-02: 表已存在且迁移已记录

- **前置条件**：表和 `__EFMigrationsHistory` 均已存在
- **步骤**：调用 `InitializeAsync`
- **预期**：无报错，不重复创建表或索引

### TC-PG-03: 表已存在但迁移未记录

- **前置条件**：表存在，`__EFMigrationsHistory` 为空或不存在
- **步骤**：调用 `InitializeAsync`
- **预期**：自动创建 `__EFMigrationsHistory` 并写入迁移记录，不重复创建表

## SQLite 初始化

### TC-SL-01: 全新数据库初始化

- **前置条件**：`ConnectionStrings:Default` 不含 `Host=` 或 `Server=`
- **步骤**：启动服务，调用 `InitializeAsync`
- **预期**：
  - 使用 `EnsureCreated` 创建所有表
  - 跳过迁移逻辑
  - 数据库文件创建在指定路径

### TC-SL-02: 数据库文件已存在

- **前置条件**：SQLite 数据库文件已存在且表完整
- **步骤**：调用 `InitializeAsync`
- **预期**：无报错，不重复创建表

## 旧表迁移

### TC-MG-01: 旧表 mistake 存在，mistake_item 不存在

- **前置条件**：存在旧表 `mistake`，不存在 `mistake_item`
- **步骤**：调用 `InitializeAsync`
- **预期**：
  - 删除旧表外键
  - 重命名 `mistake` → `mistake_item`
  - 添加缺失列（`source_upload_id`, `source_regions`, `reviewer_id`, `reviewed_at`, `root_cause`, `type`, `question_id`）
  - 重命名 `status` → `review_status`
  - 删除废弃列（`date`, `image_paths`, `fingerprint`）
  - 创建索引

### TC-MG-02: 旧表 mistake 和新表 mistake_item 同时存在

- **前置条件**：`mistake` 和 `mistake_item` 均存在
- **步骤**：调用 `InitializeAsync`
- **预期**：删除旧表 `mistake`，保留 `mistake_item`

### TC-MG-03: 旧表不存在

- **前置条件**：不存在 `mistake` 表
- **步骤**：调用 `InitializeAsync`
- **预期**：跳过旧表迁移，仅执行列名修正

### TC-MG-04: 列名修正 process_log.mistake_id → mistake_item_id

- **前置条件**：`process_log` 表存在 `mistake_id` 列
- **步骤**：调用 `InitializeAsync`
- **预期**：列重命名为 `mistake_item_id`，补充外键约束

### TC-MG-05: 列名修正 mistake_practice.mistake_id → mistake_item_id

- **前置条件**：`mistake_practice` 表存在 `mistake_id` 列
- **步骤**：调用 `InitializeAsync`
- **预期**：列重命名为 `mistake_item_id`，补充外键约束

### TC-MG-06: 外键约束已存在时不重复创建

- **前置条件**：`FK_process_log_mistake_item_mistake_item_id` 已存在
- **步骤**：调用 `InitializeAsync`
- **预期**：不重复添加外键约束

## 级联删除

### TC-CD-01: 删除 mistake_item 时级联删除 mistake_practice

- **前置条件**：`mistake_item` 有关联的 `mistake_practice` 记录
- **步骤**：删除 `mistake_item` 记录
- **预期**：关联的 `mistake_practice` 记录自动删除

### TC-CD-02: 删除 mistake_item 时级联删除 process_log

- **前置条件**：`mistake_item` 有关联的 `process_log` 记录
- **步骤**：删除 `mistake_item` 记录
- **预期**：关联的 `process_log` 记录自动删除

### TC-CD-03: 删除 mistake_item 时级联删除所有关联记录

- **前置条件**：`mistake_item` 同时关联 `mistake_practice` 和 `process_log` 记录
- **步骤**：删除 `mistake_item` 记录
- **预期**：所有关联记录均自动删除

## JSONB 序列化/反序列化

### TC-JS-01: source_regions 序列化

- **前置条件**：创建 `MistakeItemEntity`，设置 `SourceRegions`
- **步骤**：保存到数据库并读取
- **预期**：
  - JSON 使用 camelCase 命名
  - `boundingBox` 可为 null
  - 空列表序列化为 `[]`

### TC-JS-02: source_regions 变更追踪

- **前置条件**：`MistakeItemEntity` 已保存
- **步骤**：修改 `SourceRegions` 列表内容（不替换引用），调用 `SaveChanges`
- **预期**：EF Core 检测到变更并更新数据库

### TC-JS-03: image_paths 序列化

- **前置条件**：创建 `MistakePracticeEntity`，设置 `ImagePaths`
- **步骤**：保存到数据库并读取
- **预期**：
  - 序列化为字符串数组
  - 空列表序列化为 `[]`

### TC-JS-04: image_paths 变更追踪

- **前置条件**：`MistakePracticeEntity` 已保存
- **步骤**：修改 `ImagePaths` 列表内容，调用 `SaveChanges`
- **预期**：EF Core 检测到变更并更新数据库

### TC-JS-05: SQLite 下 JSONB 列兼容性

- **前置条件**：使用 SQLite 数据库
- **步骤**：保存和读取含 JSONB 列的实体
- **预期**：序列化/反序列化正常工作，数据以 text 形式存储

## 乐观并发

### TC-OC-01: 并发更新冲突检测

- **前置条件**：两个上下文读取同一条 `mistake_item` 记录
- **步骤**：两个上下文分别修改并保存
- **预期**：第二个保存抛出 `DbUpdateConcurrencyException`
