# ruoyu.mistake — 文档地图

## 顶层总览

| 文档 | 用途 |
|------|------|
| [SystemContext.md](./SystemContext.md) | 服务定位、上下游调用关系、边界 |
| [Integration.md](./Integration.md) | 集成矩阵、接口边界、失败语义 |
| [KeyFlows.md](./KeyFlows.md) | 关键跨服务时序与调用链 |
| [DataOwnership.md](./DataOwnership.md) | 数据主责、引用边界、双写禁区 |
| [Requirements.md](./Requirements.md) | 服务级需求概述 |
| [Design.md](./Design.md) | 服务级架构概述 |
| [DotNetCodingPolicy.md](./DotNetCodingPolicy.md) | 代码规范 |
| [Deployment.md](./Deployment.md) | 部署说明 |
| [Troubleshooting.md](./Troubleshooting.md) | 故障排查指南 |
| [IntegrationVerification.md](./IntegrationVerification.md) | 集成验证 |

## 统一事实源

| 文档 | 用途 |
|------|------|
| [database/README.md](./database/README.md) | 数据库表清单、实体关系、迁移历史 |
| [database/relations.md](./database/relations.md) | 实体关系图 |
| [database/migrations.md](./database/migrations.md) | 迁移历史 |
| [database/tables/mistake_item.md](./database/tables/mistake_item.md) | 错题条目表 |
| [database/tables/mistake_practice.md](./database/tables/mistake_practice.md) | 错题练习记录表 |
| [database/tables/process_log.md](./database/tables/process_log.md) | 处理日志表 |

## 内部业务能力

| 业务域 | 功能点 | 入口 |
|--------|--------|------|
| MistakeDispatch | SubmitMistakeUpload | [01-FEATURE](./modules/MistakeDispatch/SubmitMistakeUpload/01-FEATURE.md) |
| MistakeReview | ReviewMistakeItem | [01-FEATURE](./modules/MistakeReview/ReviewMistakeItem/01-FEATURE.md) |
| MistakeReview | GetPendingReviewUploads | [01-FEATURE](./modules/MistakeReview/GetPendingReviewUploads/01-FEATURE.md) |
| MistakeReview | CompleteUploadReview | [01-FEATURE](./modules/MistakeReview/CompleteUploadReview/01-FEATURE.md) |
| MistakeManagement | GetMistakeItem | [01-FEATURE](./modules/MistakeManagement/GetMistakeItem/01-FEATURE.md) |
| MistakeManagement | GetMistakeItemsByUpload | [01-FEATURE](./modules/MistakeManagement/GetMistakeItemsByUpload/01-FEATURE.md) |
| MistakeManagement | GetMistakeItemList | [01-FEATURE](./modules/MistakeManagement/GetMistakeItemList/01-FEATURE.md) |
| MistakeManagement | AddMistakeItem | [01-FEATURE](./modules/MistakeManagement/AddMistakeItem/01-FEATURE.md) |
| MistakeManagement | UpdateMistakeItem | [01-FEATURE](./modules/MistakeManagement/UpdateMistakeItem/01-FEATURE.md) |
| MistakeManagement | DeleteMistakeItem | [01-FEATURE](./modules/MistakeManagement/DeleteMistakeItem/01-FEATURE.md) |
| MistakePractice | AddMistakePractice | [01-FEATURE](./modules/MistakePractice/AddMistakePractice/01-FEATURE.md) |
| MistakePractice | GetMistakePractices | [01-FEATURE](./modules/MistakePractice/GetMistakePractices/01-FEATURE.md) |
| MistakeProcessLogging | LogMistakeProcess | [01-FEATURE](./modules/MistakeProcessLogging/LogMistakeProcess/01-FEATURE.md) |
| MistakeProcessLogging | GetMistakeProcessHistory | [01-FEATURE](./modules/MistakeProcessLogging/GetMistakeProcessHistory/01-FEATURE.md) |
| MistakeAnalysis | AnalyzeMistakeType | [01-FEATURE](./modules/MistakeAnalysis/AnalyzeMistakeType/01-FEATURE.md) |
| ImagePathQuery | GetAllReferencedImagePaths | [01-FEATURE](./modules/ImagePathQuery/GetAllReferencedImagePaths/01-FEATURE.md) |

完整索引见 [modules/README.md](./modules/README.md)

## 外部系统交互

| 外部系统 | 功能点 | 入口 |
|----------|--------|------|
| Student | 学生服务集成 | [01-FEATURE](./Integration/Student/01-FEATURE.md) |
| OSS | 对象存储集成 | [01-FEATURE](./Integration/Oss/01-FEATURE.md) |
| Storage | 存储服务集成 | [01-FEATURE](./Integration/Storage/01-FEATURE.md) |
| Portals | 前端门户集成 | [01-FEATURE](./Integration/Portals/01-FEATURE.md) |

完整索引见 [Integration/README.md](./Integration/README.md)

## 阅读建议

1. **第一次阅读**: SystemContext.md → Design.md → KeyFlows.md
2. **查某个功能**: modules/README.md → 对应功能点 01-FEATURE.md
3. **查数据库**: database/README.md → database/tables/具体表名.md
4. **查集成**: Integration.md → Integration/具体系统/
5. **查部署**: Deployment.md
