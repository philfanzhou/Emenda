# Agent Review Notes

## 1. 本轮任务范围

为 ruoyu.mistake 项目基于现有代码优化 docs 目录，重点：
- 修正上一轮文档生成中的编造内容（`RemoveImagesFromRecord`）
- 修正断链和路径错误
- 精简顶层文档，确保细节下沉到 feature 6 件套
- 标注推断内容

## 2. 已完成的文档调整

### 重大修正：RemoveImagesFromRecord 编造内容清除

上一轮文档生成时错误地编造了 `StudentLearningGrpcService.RemoveImagesFromRecord` 这一 gRPC 调用，实际代码中不存在任何 gRPC client 注册或调用。本轮修正涉及：

- **SystemContext.md** — 删除下游依赖中的 ruoyu.student 小节，修正上下文图和服务边界
- **Integration.md** — 删除 RemoveImagesFromRecord 集成条目和失败语义行
- **KeyFlows.md** — 删除审核流程中 student 回调步骤
- **Design.md** — 删除依赖关系图中的 StudentLearningGrpcServiceClient
- **Integration/Student/** — 重写全部 6 件套，改为描述 student_id 只读引用
- **Integration/README.md** — 修正 Student 集成描述
- **Integration/Portals/04-TASKS.md** — 删除 StudentLearningGrpcServiceClient 注册任务
- **Troubleshooting.md** — 删除 RemoveImagesFromRecord 相关故障排查条目
- **IntegrationVerification.md** — 删除 RemoveImagesFromRecord 相关验证用例
- **Deployment.md** — 删除 StudentGrpcService:Address 配置和联调步骤
- **Requirements.md** — 删除"回调 student 清理引用"描述

### 断链修正

- **Requirements.md** — 功能需求索引链接从旧路径（如 `MistakeDispatch/SubmitMistakeUpload/`）修正为 `./modules/MistakeDispatch/SubmitMistakeUpload/01-FEATURE.md`
- **Design.md** — 详细设计索引链接从旧路径修正为 `./modules/.../03-DESIGN.md`，并展开为每个功能点的独立链接

## 3. 待人工审核事项

### 3.1 AnalyzeMistakeType 硬编码实现

- **问题描述**: `AnalyzeMistakeTypeAsync` 当前返回硬编码值 (type=5, typeName="其他错误")，proto 中定义了 content/correct_answer/student_answer 参数但 Domain Service 未使用
- **已查阅的代码**: MistakeDomainService.cs, mistake.proto
- **为什么仍无法完全确认**: 无法确定这是"待实现的占位逻辑"还是"有意为之的 MVP 策略"
- **建议人工如何复核**: 确认产品路线图中错题分析的优先级和时间表

### 3.2 LogMistakeProcess 的 studentId 参数未使用

- **问题描述**: `LogMistakeProcessAsync` 接受 `studentId` 参数但未存储到 ProcessLog 中
- **已查阅的代码**: MistakeDomainService.cs, ProcessLogModel.cs
- **为什么仍无法完全确认**: ProcessLogModel 没有 StudentId 字段，但 proto 定义了该参数
- **建议人工如何复核**: 确认是否需要在 process_log 表中增加 student_id 字段

### 3.3 AddMistakeItem 与 SubmitMistakeUpload 的验证范围差异

- **问题描述**: AddMistakeItem 的 subject 范围为 0-9（允许占位值），SubmitMistakeUpload 为 1-9
- **已查阅的代码**: MistakeDomainService.cs (ValidateSubmitData vs ValidateMistakeItemData)
- **为什么仍无法完全确认**: 可能是有意设计（手动添加允许未指定学科），也可能是遗漏
- **建议人工如何复核**: 确认业务规则是否允许手动添加时学科为0

### 3.4 UpdateMistakeItem 的部分更新语义在 gRPC 层

- **问题描述**: UpdateMistakeItem 的字段选择逻辑（仅更新非空/非零字段）在 MistakeServiceImpl 而非 Domain Service 中
- **已查阅的代码**: MistakeServiceImpl.cs
- **为什么仍无法完全确认**: 这违反了"胖领域服务"的设计模式，但可能是为了保持 Domain Service 的通用性
- **建议人工如何复核**: 确认是否应将部分更新逻辑下沉到 Domain Service

### 3.5 CompleteUploadReview 的非事务性图片迁移

- **问题描述**: CompleteUploadReview 中图片迁移（OSS Copy+Delete）与数据库更新不在同一事务中，OSS 失败仅记日志不中断
- **已查阅的代码**: MistakeServiceImpl.cs
- **为什么仍无法完全确认**: 这可能导致图片迁移不完整但数据库状态已更新
- **建议人工如何复核**: 评估是否需要引入补偿机制或重试队列

## 4. 证据不足但已落盘的内容

| 编号 | 内容 | 标注 | 位置 |
|------|------|------|------|
| E-01 | SystemContext.md 中上游调用方的角色划分（admin_portal/teacher_portal/user_portal） | [推断] | SystemContext.md 上游节 |
| E-02 | AnalyzeMistakeType 的真正分析逻辑 | [待实现] | MistakeAnalysis/AnalyzeMistakeType/02-SPEC.md |

## 5. 风险与后续建议

1. **测试覆盖不足**: 16 个功能点中仅 7 个有单元测试覆盖，建议优先补充 GetMistakeItem、GetMistakeItemsByUpload、GetMistakeItemList 的测试
2. **AnalyzeMistakeType 占位实现**: 当前硬编码返回"其他错误"，需确认产品路线图
3. **全表扫描性能**: GetAllReferencedImagePaths 加载全表到内存，数据量大时可能有性能问题
4. **SQLite 兼容性**: 多处查询因 SQLite 不支持 DateTimeOffset 排序/聚合而在客户端处理，生产环境 PostgreSQL 不需要这些 workaround
5. **文档与代码同步**: 建议在 CI 中加入文档链接检查，防止后续代码变更导致文档失效
6. **上一轮编造内容遗留**: 虽然已清除 56 处 RemoveImagesFromRecord 引用，但建议人工抽查 Integration/Student/ 和 Troubleshooting.md 确认修正完整
