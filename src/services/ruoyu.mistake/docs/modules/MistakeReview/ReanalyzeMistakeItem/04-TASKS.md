# ReanalyzeMistakeItem — 任务清单 (TASKS)

> 任务按依赖顺序排列。

## 1. 文档先行

- [x] TASK-01: 创建 `ReanalyzeMistakeItem/01-FEATURE.md`
- [x] TASK-02: 创建 `ReanalyzeMistakeItem/02-SPEC.md`
- [x] TASK-03: 创建 `ReanalyzeMistakeItem/03-DESIGN.md`
- [x] TASK-04: 创建 `ReanalyzeMistakeItem/05-TESTS.md`
- [x] TASK-05: 创建 `ReanalyzeMistakeItem/06-CONVENTIONS.md`
- [x] TASK-06: 更新 `ReviewMistakeItem/01-FEATURE.md` 提及"审核时可重新分析"
- [x] TASK-07: 更新 `MistakeList/02-SPEC.md`（Mistake 服务侧）新增 FR
- [x] TASK-08: 更新 `teacher_portal/MistakeList/02-SPEC.md` 新增 FR + AC-FR-10 鉴权边界
- [x] TASK-09: 更新 `teacher_portal/Integration/MistakeService/03-DESIGN.md` 集成示例
- [x] TASK-10: 更新 `teacher_portal/Integration/MistakeService/02-SPEC.md` gRPC 方法清单

## 2. Proto 先行

- [ ] TASK-11: `mistake.common.proto` 新增 `PROCESS_ACTION_REANALYZED = 7`
- [ ] TASK-12: `mistake.proto` 新增 `ReanalyzeMistakeItemRequest` / `ReanalyzeMistakeItemResponse`
- [ ] TASK-13: `mistake.proto` 新增 `rpc ReanalyzeMistakeItem`

## 3. VL Analyzer

- [ ] TASK-14: `IVlAnalyzer.cs` 新增 `ReAnalyzeAsync` 接口方法
- [ ] TASK-15: `IVlAnalyzer.cs` 新增 `VlReanalysisResult` 类
- [ ] TASK-16: `VlUploadAnalyzer.cs` 实现 `ReAnalyzeAsync`
- [ ] TASK-17: `VlUploadAnalyzer.cs` 新增 `BuildReanalyzePrompt` 私有方法
- [ ] TASK-18: `VlUploadAnalyzer.cs` 新增 `ParseReanalyzeResponse` 私有方法

## 4. Domain + gRPC

- [ ] TASK-19: `IMistakeDomainService.cs` 新增 `ReanalyzeMistakeItemAsync` 接口
- [ ] TASK-20: `MistakeDomainService.cs` 实现 `ReanalyzeMistakeItemAsync`
- [ ] TASK-21: `MistakeServiceImpl.cs` 新增 `ReanalyzeMistakeItem` gRPC override
- [ ] TASK-22: `MistakeServiceImpl.cs` 新增 `MapMistakeItemDto`（已存在，复用）

## 5. Teacher Portal BFF

- [ ] TASK-23: `AuthModels.cs` 新增 `ReanalyzeMistakeRequest` / `ReanalyzeMistakeResponse`
- [ ] TASK-24: `MistakeController.cs` 新增 `POST /api/mistake/{id}/reanalyze`
- [ ] TASK-25: `MistakeControllerTests.cs` 新增 BFF 单元测试

## 6. Teacher Portal Frontend

- [ ] TASK-26: `ReviewDetail.vue` 新增"AI 重新分析"按钮 + 模态框
- [ ] TASK-27: `RecordDetail.vue` 新增"AI 重新分析"按钮 + 模态框
- [ ] TASK-28: `types/index.ts` 新增 `ReanalyzeMistakeItemRequest` 类型

## 7. 测试

- [ ] TASK-29: `MistakeDomainServiceTests.cs` 新增 ReanalyzeMistakeItemAsync 测试
- [ ] TASK-30: `MistakeServiceImplRpcTests.cs` 新增 ReanalyzeMistakeItem gRPC 测试
- [ ] TASK-31: `MistakeControllerTests.cs`（teacher_portal）新增 BFF 测试

## 8. 验证

- [ ] TASK-32: 运行 `./tests/integration/scripts/pre-commit.sh --skip-docker --skip-e2e --skip-integration`
- [ ] TASK-33: 确认 admin_portal **不**含 re-analyze 端点
- [ ] TASK-34: 3 个前端构建 + 2 个 BFF 后端 + Mistake 服务构建
