# Agent General Change Audit

## 1. 本轮任务范围

处理 1 个输入项：
- ITEM-04: Mistake 服务启动失败 — process_log 表 student_id 列已存在导致 EF Core 迁移报错

## 2. 启动校验结果

- 目标项目路径存在且可访问: ✅ (`backend/ruoyu.mistake`)
- 正式 docs 根目录存在: ✅ (`backend/ruoyu.mistake/docs`)
- 输入项可解析: ✅ (PostgresException 42701, column "student_id" already exists)
- 相关模块、文档入口、代码入口已定位: ✅
- 工作区无无关未提交改动: ✅

## 3. 输入项总表

| 编号 | 类型 | 标题 | 关联模块 | 当前状态 |
|------|------|------|----------|----------|
| ITEM-04 | bug | Mistake 服务启动失败 — process_log.student_id 列已存在导致迁移崩溃 | DatabaseInitializer, Migrations | 已完成 |

## 4. 每个输入项的处理记录

### ITEM-04: Mistake 服务启动失败 — process_log.student_id 列已存在

- **输入摘要**: 启动 Mistake 服务时报 `42701: column "student_id" of relation "process_log" already exists`，服务崩溃无法启动
- **已核查的证据**:
  - 错误日志: `ALTER TABLE process_log ADD student_id character varying(36)` → SqlState 42701
  - 调用栈: `DatabaseInitializer.InitializeAsync` L88 → `MistakeDatabaseInitializer.InitializeAsync` L18
  - 迁移文件 `20260612030015_AddStudentIdToProcessLog.cs`: Up() 中直接 `AddColumn("student_id")` 无 IF NOT EXISTS 保护
  - `EnsureColumnRenamesAsync` (DatabaseInitializer.cs L227-238): 已通过 `IF NOT EXISTS` 幂等地添加 student_id 列
  - `StampMigrationsIfTablesExistAsync` (DatabaseInitializer.cs L21-76): 只 stamp 了 `InitialCreate`，未 stamp `AddStudentIdToProcessLog`
- **根因分析**:
  1. `EnsureColumnRenamesAsync` 在启动时先执行，通过 `IF NOT EXISTS` 安全地添加了 `student_id` 列
  2. `StampMigrationsIfTablesExistAsync` 只将 `InitialCreate` 标记为已应用，未将 `AddStudentIdToProcessLog` 也标记
  3. `MigrateAsync` 发现 `AddStudentIdToProcessLog` 为 pending，尝试执行 `ALTER TABLE ADD COLUMN student_id`
  4. 列已存在 → PostgresException 42701 → 服务崩溃
- **影响面判断**: 所有从旧表迁移过来的生产环境数据库都会触发此问题（因为 EnsureColumnRenamesAsync 会先添加列）
- **文档修改项**:
  - `docs/database/migrations.md`: 新增 `AddStudentIdToProcessLog` 迁移记录，说明幂等性问题和 stamp 机制
  - `docs/development/Troubleshooting.md`: 新增 5.5 节 "迁移列已存在导致启动失败"
- **文档校验结果**: ✅ 通过
- **代码修改项**:
  - `DatabaseInitializer.cs` `StampMigrationsIfTablesExistAsync`: 增加 `AddStudentIdToProcessLog` 迁移的 stamp 逻辑，当 process_log 表已存在 student_id 列时标记该迁移为已应用
- **验证结果**: 代码逻辑审查通过，确保 stamp 在 MigrateAsync 之前执行

## 5. 正式文档同步记录

| 文档路径 | 更新原因 | 更新摘要 |
|----------|----------|----------|
| docs/database/migrations.md | ITEM-04 | 新增 AddStudentIdToProcessLog 迁移记录，说明幂等性问题 |
| docs/development/Troubleshooting.md | ITEM-04 | 新增 5.5 节 "迁移列已存在导致启动失败" |
| docs/AgentGeneralChangeAudit.md | ITEM-04 | 本轮审计记录 |

## 6. 未完成或暂时跳过的输入项

无。

## 7. 风险与待人工复核事项

- 部署后需确认：对于已有 student_id 列的生产数据库，`StampMigrationsIfTablesExistAsync` 能正确识别并标记 `AddStudentIdToProcessLog` 为已应用
- 若数据库中 `__EFMigrationsHistory` 已有 `AddStudentIdToProcessLog` 记录，stamp 逻辑不会重复插入（已有 COUNT > 0 检查）

---

## 历史记录

### 上一轮 (ITEM-01/02/03)

处理 3 个输入项：
- ITEM-01: 移除 AnalyzeMistakeType 功能（不需要的业务）
- ITEM-02: 非事务性图片迁移风险 - 提供细节并评估
- ITEM-03: gRPC 层集成测试缺失 - 用 UT 提高覆盖率

详见 git 历史中的上一版本文档。
