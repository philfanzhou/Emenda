# 总览文档索引

本目录收拢 ruoyu.mistake 服务的所有服务级总览类正式文档。本目录只负责全局认知、边界和导航，不替代 `modules/`、`Integration/`、`database/`、`development/` 的细节文档。

## 文档清单

| 文档 | 用途 | 下钻目标 |
|------|------|----------|
| [SystemContext.md](./SystemContext.md) | 服务定位、上下游调用关系、边界 | [Integration/](../Integration/) |
| [Integration.md](./Integration.md) | 集成矩阵、接口边界、失败语义 | [Integration/](../Integration/) |
| [KeyFlows.md](./KeyFlows.md) | 关键跨服务时序与调用链 | [modules/](../modules/) |
| [DataOwnership.md](./DataOwnership.md) | 数据主责、引用边界、双写禁区 | [database/](../database/) |
| [Requirements.md](./Requirements.md) | 服务级需求概述 | [modules/](../modules/) |
| [Design.md](./Design.md) | 服务级架构概述 | [modules/](../modules/) |

## 阅读建议

1. **第一次阅读**: SystemContext.md → Design.md → KeyFlows.md
2. **查集成**: Integration.md → [Integration/](../Integration/) 目录
3. **查数据**: DataOwnership.md → [database/](../database/) 目录
4. **查需求**: Requirements.md → [modules/](../modules/) 目录

## 下一步

读完总览后，按需进入专题目录：
- 查某个功能的详细需求/设计/测试 → [modules/](../modules/README.md)
- 查外部系统交互细节 → [Integration/](../Integration/README.md)
- 查表结构/索引/迁移 → [database/](../database/README.md)
- 查部署/排障/联调 → [development/](../development/README.md)
