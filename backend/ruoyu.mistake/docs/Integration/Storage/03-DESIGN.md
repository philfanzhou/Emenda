# 设计说明

## 1. 数据库选择逻辑

```
连接字符串包含 "Host=" 或 "Server=" ?
  ├─ 是 → PostgreSQL (UseNpgsql)
  └─ 否 → SQLite (UseSqlite)
           └─ 连接字符串为空时使用默认值:
              Data Source=data/sqlite/ruoyu_study_mistake.db
```

判断逻辑位于 `Program.cs`，通过检查 `ConnectionStrings:Default` 是否包含 `Host=` 或 `Server=`（不区分大小写）来决定使用哪种数据库提供程序。

## 2. 初始化流程

### PostgreSQL 模式

```
InitializeAsync(context, logger)
  │
  ├─ 1. MigrateOldSchemaAsync
  │     检测旧表 mistake → 迁移到 mistake_item
  │
  ├─ 2. StampMigrationsIfTablesExistAsync
  │     表已存在但 __EFMigrationsHistory 为空 → 自动 stamp
  │
  └─ 3. Common.Database.DatabaseInitializer.InitializeAsync
        使用 GetTableCreationSql 创建缺失的表和索引
```

### SQLite 模式

```
InitializeAsync(context, logger)
  │
  └─ Common.Database.DatabaseInitializer.InitializeAsync
       使用 EnsureCreated 确保表存在（跳过迁移逻辑）
```

SQLite 模式通过检测 `context.Database.ProviderName` 是否包含 "Sqlite" 来识别，跳过所有 PostgreSQL 特有的迁移逻辑。

## 3. 旧表迁移流程

```
MigrateOldSchemaAsync
  │
  ├─ 检测旧表 mistake 是否存在
  │   ├─ 不存在 → 执行 EnsureColumnRenamesAsync → 结束
  │   └─ 存在 → 继续
  │
  ├─ 删除旧表 mistake 上的所有外键约束
  │
  ├─ 检测 mistake_item 是否已存在
  │   ├─ 已存在 → DROP TABLE mistake CASCADE
  │   └─ 不存在 → 继续
  │
  ├─ ALTER TABLE mistake RENAME TO mistake_item
  │
  ├─ 添加缺失列:
  │   - source_upload_id (varchar(64), DEFAULT '')
  │   - source_regions (jsonb, DEFAULT '[]')
  │   - reviewer_id (varchar(36), NULL)
  │   - reviewed_at (timestamptz, NULL)
  │   - review_comment (text, NULL)
  │   - type (integer, DEFAULT 0)
  │   - question_id (varchar(36), NULL)
  │
  ├─ 重命名列: status → review_status
  │
  ├─ 删除废弃列: date, image_paths, fingerprint
  │
  ├─ 创建索引:
  │   - IX_mistake_item_student_id
  │   - IX_mistake_item_student_id_subject_grade_review_status
  │   - IX_mistake_item_source_upload_id
  │   - IX_mistake_item_review_status
  │   - IX_mistake_item_created_at
  │
  └─ EnsureColumnRenamesAsync
      ├─ process_log.mistake_id → mistake_item_id
      │   + 补充外键 FK_process_log_mistake_item_mistake_item_id (ON DELETE CASCADE)
      └─ mistake_practice.mistake_id → mistake_item_id
          + 补充外键 FK_mistake_practice_mistake_item_mistake_item_id (ON DELETE CASCADE)
```

## 4. JSONB 序列化设计

### source_regions（MistakeItemEntity）

- **序列化**：`JsonSerializer.Serialize(value, JsonOptions)` — 使用 `CamelCase` 命名策略
- **反序列化**：`JsonSerializer.Deserialize<List<SourceRegionEntry>>(value, JsonOptions)`
- **ValueComparer**：
  - 相等判断：`SequenceEqual`
  - 哈希计算：聚合 `SourceImagePath` 和 `BoundingBox` 的哈希
  - 快照：序列化后反序列化实现深拷贝

### image_paths（MistakePracticeEntity）

- **序列化**：`JsonSerializer.Serialize(value, null)` — 不使用命名策略
- **反序列化**：`JsonSerializer.Deserialize<List<string>>(value, null)`
- **ValueComparer**：
  - 相等判断：`SequenceEqual`
  - 哈希计算：聚合每个字符串的哈希
  - 快照：`ToList()` 实现浅拷贝（string 不可变，浅拷贝安全）

### SQLite 兼容

SQLite 不支持原生 JSONB 类型，`image_paths` 列使用 `text` 类型存储（`DEFAULT '[]'`），EF Core 的 `HasColumnType("jsonb")` 在 SQLite 下会被忽略，序列化/反序列化逻辑仍然正常工作。

## 5. 级联删除设计

```
mistake_item (主表)
  │
  ├─ mistake_practice.mistake_item_id → ON DELETE CASCADE
  │   删除错题时，关联练习记录自动删除
  │
  └─ process_log.mistake_item_id → ON DELETE CASCADE
      删除错题时，关联流程日志自动删除
```

- EF Core 配置：`OnDelete(DeleteBehavior.Cascade)`
- 数据库约束：`FOREIGN KEY (mistake_item_id) REFERENCES mistake_item(id) ON DELETE CASCADE`
- 旧表迁移时自动补充缺失的外键约束
- 外键约束名称遵循格式：`FK_{子表}_{主表}_{外键列}`
