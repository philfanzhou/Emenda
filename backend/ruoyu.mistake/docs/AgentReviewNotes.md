# Agent Review Notes

## 1. 本轮任务范围

为 ruoyu.mistake 项目基于现有代码优化 docs 目录，重点：
- 增强 README 自解释能力（目录职责、功能点分布、下钻指引）
- 修正 Integration/README.md 中的断链
- 补充 overview/README.md 的"下一步"导航

## 2. 已完成的文档调整

### 本轮调整

- **docs/README.md** — 新增"目录职责"表格（每个目录的职责和内容概要）和"功能点分布"表格（7 个业务域的核心能力），让 Agent 仅通过本文件就能知道各目录负责什么、功能点在哪里
- **overview/README.md** — 新增"下钻目标"列和"下一步"节，让 Agent 读完总览后明确知道应进入哪个专题目录；补充"本目录不替代细节文档"声明
- **Integration/README.md** — 修复 2 处断链：`../Deployment.md` → `../development/Deployment.md`，`../Troubleshooting.md` → `../development/Troubleshooting.md`

### 历史调整（保留记录）

- 创建 `docs/overview/` — 收拢 7 个总览类文档
- 创建 `docs/development/` — 收拢 3 个开发执行文档
- 重写 `docs/README.md` 为纯入口导航
- 清除 RemoveImagesFromRecord 编造内容（11 个文件，56 处引用）
- 修正 Requirements.md 和 Design.md 的断链

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
| E-01 | SystemContext.md 中上游调用方的角色划分（admin_portal/teacher_portal/user_portal） | [推断] | overview/SystemContext.md 上游节 |
| E-02 | AnalyzeMistakeType 的真正分析逻辑 | [待实现] | modules/MistakeAnalysis/AnalyzeMistakeType/02-SPEC.md |

## 5. 风险与后续建议

1. **测试覆盖不足**: 16 个功能点中仅 7 个有单元测试覆盖，建议优先补充 GetMistakeItem、GetMistakeItemsByUpload、GetMistakeItemList 的测试
2. **AnalyzeMistakeType 占位实现**: 当前硬编码返回"其他错误"，需确认产品路线图
3. **全表扫描性能**: GetAllReferencedImagePaths 加载全表到内存，数据量大时可能有性能问题
4. **SQLite 兼容性**: 多处查询因 SQLite 不支持 DateTimeOffset 排序/聚合而在客户端处理，生产环境 PostgreSQL 不需要这些 workaround
5. **文档与代码同步**: 建议在 CI 中加入文档链接检查，防止后续代码变更导致文档失效
