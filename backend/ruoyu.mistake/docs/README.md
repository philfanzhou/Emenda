# ruoyu.mistake — 文档入口

本目录是 ruoyu.mistake 错题本服务的文档根入口。`docs/` 根目录只保留导航入口和审阅记录，正式文档按职责分布在子目录中。

## 导航

| 入口 | 用途 |
|------|------|
| [overview/](./overview/README.md) | 服务级总览文档（系统上下文、集成、流程、需求、设计、规范） |
| [modules/](./modules/README.md) | 内部业务能力（16 个功能点的 6 件套） |
| [Integration/](./Integration/README.md) | 外部系统交互（Student、OSS、Storage、Portals） |
| [database/](./database/README.md) | 数据库唯一事实源（表结构、索引、关系、迁移） |
| [development/](./development/README.md) | 开发执行支持（部署、排障、集成验证） |
| [AgentReviewNotes.md](./AgentReviewNotes.md) | 审阅记录（遗留问题、证据缺口、待人工确认项） |

## 阅读建议

1. **第一次阅读**: [overview/](./overview/README.md) → SystemContext.md → Design.md → KeyFlows.md
2. **查某个功能**: [modules/](./modules/README.md) → 对应功能点 01-FEATURE.md
3. **查数据库**: [database/](./database/README.md) → tables/具体表名.md
4. **查集成**: [overview/Integration.md](./overview/Integration.md) → [Integration/](./Integration/README.md)
5. **查部署/排障**: [development/](./development/README.md)
6. **查遗留问题**: [AgentReviewNotes.md](./AgentReviewNotes.md)
