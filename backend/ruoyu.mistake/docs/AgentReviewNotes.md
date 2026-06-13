吗dd# Agent Review Notes

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

## 3. 人工批注处理状态

所有人工批注已在本轮及前几轮执行中闭环处理：

| 编号 | 批注摘要 | 处理结果 |
|------|---------|---------|
| 3.1 | AnalyzeMistakeType 硬编码实现，价值不大可移除 | 已移除：proto RPC、消息定义、ServiceImpl 方法、DomainService 方法、测试、文档、GrpcTester 按钮 |
| 3.2 | LogMistakeProcess studentId 参数未使用，需要增加 | 已增加：ProcessLogModel/Entity/Dto 添加 StudentId，DatabaseInitializer 自动迁移 |
| 3.3 | AddMistakeItem subject 验证范围 0-9 是错误 | 已修复：ValidateMistakeItemData 改为 1-9 |
| 3.4 | UpdateMistakeItem 部分更新逻辑需要下沉到 DomainService | 已下沉：新增 UpdateMistakeItemPartialAsync |
| 3.5 | CompleteUploadReview 非事务性图片迁移需要更多信息 | 已改进：Copy 成功即更新路径，Delete 失败仅 Warning；11 个 Service 层 UT 验证 |
| 5.1 | 测试覆盖不足，需要补齐 | 已补齐：68 个测试（57 Domain + 11 Service）全部通过 |
| 5.2 | AnalyzeMistakeType 占位实现需移除 | 同 3.1，已移除 |
| 5.3 | 全表扫描性能问题 | 已修复：改用投影查询（GetAllSourceImagePathsAsync/GetAllPracticeImagePathsAsync） |
| 5.4 | SQLite 兼容性 workaround | 已文档标注为已知设计决策（Deployment.md） |
| 5.5 | 文档链接检查 | 已检查修复，IntegrationVerification.md 移除 AnalyzeMistakeType 引用 |

## 4. 证据不足但已落盘的内容

| 编号 | 内容 | 标注 | 位置 | 当前状态 |
|------|------|------|------|---------|
| E-01 | SystemContext.md 中上游调用方的角色划分 | [推断] | overview/SystemContext.md 上游节 | 保持推断，不影响功能 |
| E-02 | AnalyzeMistakeType 的真正分析逻辑 | [待实现] | 已删除 | 功能已整体移除 |

## 5. 风险与后续建议

无待处理风险。所有历史批注已闭环。
