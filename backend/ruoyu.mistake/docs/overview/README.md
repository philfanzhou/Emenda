# 总览文档索引

本目录收拢 ruoyu.mistake 服务的所有服务级总览类正式文档。

## 文档清单

| 文档 | 用途 |
|------|------|
| [SystemContext.md](./SystemContext.md) | 服务定位、上下游调用关系、边界 |
| [Integration.md](./Integration.md) | 集成矩阵、接口边界、失败语义 |
| [KeyFlows.md](./KeyFlows.md) | 关键跨服务时序与调用链 |
| [DataOwnership.md](./DataOwnership.md) | 数据主责、引用边界、双写禁区 |
| [Requirements.md](./Requirements.md) | 服务级需求概述（详细需求见 modules/） |
| [Design.md](./Design.md) | 服务级架构概述（详细设计见 modules/） |
| [DotNetCodingPolicy.md](./DotNetCodingPolicy.md) | 代码规范 |

## 阅读建议

1. **第一次阅读**: SystemContext.md → Design.md → KeyFlows.md
2. **查集成**: Integration.md → [Integration/](../Integration/) 目录
3. **查数据**: DataOwnership.md → [database/](../database/) 目录
4. **查需求**: Requirements.md → [modules/](../modules/) 目录
5. **查规范**: DotNetCodingPolicy.md
