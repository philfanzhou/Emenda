# ruoyu.mistake — 文档入口

本目录是 ruoyu.mistake 错题本服务的文档根入口。`docs/` 根目录只保留导航入口和审阅记录，正式文档按职责分布在子目录中。

## 目录职责

| 目录 | 职责 | 内容概要 |
|------|------|----------|
| [overview/](./overview/README.md) | 服务级总览 | 系统上下文、集成矩阵、关键流程、数据主责、需求摘要、架构概述、代码规范 |
| [modules/](./modules/README.md) | 内部业务能力 | 6 个业务域、15 个功能点，每个功能点 6 件套（FEATURE/SPEC/DESIGN/TASKS/TESTS/CONVENTIONS） |
| [Integration/](./Integration/README.md) | 外部系统交互 | Student 数据引用、OSS 图片迁移、Portal 接口分配、Storage 数据库配置 |
| [database/](./database/README.md) | 数据库唯一事实源 | 3 张表（mistake_item/mistake_practice/process_log）的字段、索引、关系、迁移 |
| [development/](./development/README.md) | 开发执行支持 | 部署说明、故障排查、集成验证 |
| [AgentReviewNotes.md](./AgentReviewNotes.md) | 审阅记录 | 遗留问题、证据缺口、待人工确认项 |

## 功能点分布

本服务 15 个功能点按业务域分布在 [modules/](./modules/README.md) 中：

| 业务域 | 功能点数 | 核心能力 |
|--------|---------|----------|
| MistakeDispatch | 1 | 揅题上传提交 |
| MistakeReview | 3 | 审核、待审核查询、批量审核完成 |
| MistakeManagement | 6 | 增删改查错题条目 |
| MistakePractice | 2 | 练习记录增查 |
| MistakeProcessLogging | 2 | 处理日志增查 |
| ImagePathQuery | 1 | 引用图片路径查询 |

## 阅读建议

1. **第一次阅读**: [overview/](./overview/README.md) → SystemContext.md → Design.md → KeyFlows.md
2. **查某个功能**: [modules/](./modules/README.md) → 找到业务域 → 进入功能点 01-FEATURE.md
3. **查数据库**: [database/](./database/README.md) → tables/具体表名.md
4. **查集成**: [overview/Integration.md](./overview/Integration.md) → [Integration/](./Integration/README.md)
5. **查部署/排障**: [development/](./development/README.md)
6. **查遗留问题**: [AgentReviewNotes.md](./AgentReviewNotes.md)
