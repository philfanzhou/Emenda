# LayoutJobs — 版面检测作业受管操作

受管版面检测作业的管理端操作面（IKQA2P）：在既有受管来源修订上以已认证 Admin 身份启动版面作业（`POST /api/mistakes/intakes/{sourceId}/{revision}/layout`，请求体 `{ "requestKey": "<uuid>" }`）并读取持久状态与接受结果（`GET` 同路径）。操作沿 intake 家族受管端点约定：始终要求已认证 Admin subject，无匿名兼容窗口；作业与结果的持久语义见[layout_job 表](../database/tables/layout_job.md)，上游消费契约见 [StructaDoc 集成](../Integration/StructaDoc.md)，wire 形状以[版本化机器合同](../Integration/MachineHttpContract.md)为唯一权威（additive 登记于 mistake-http-v3）。

## 语义

- **start**：委托入队命令——同 caller/action/source 且同 `requestKey` 重放同一作业与其捕获的非秘密配置（含 credential 引用键名，永不含凭据值）；冲突输入 409 `request_payload_conflict`；新键产生下一个独立 retained result revision。排队作业立即返回（接受推理是 durable worker 的工作，不是该次 HTTP 交换）。未受管来源 404 `source_not_managed`；缺 `requestKey` 400 `invalid_request`。
- **status**：返回该来源修订全部作业（按 result revision 排序）的 raw durable 视图——每图状态与有界 attempts、接受结果的 validated regions（top-left-origin-normalized 存储帧）与捕获配置。空列表是合法答案。响应不含凭据、凭据值或签名 URL。
- **能力关闭**（`Mistake:Layout:Enabled=false` 重启后）：start 拒绝新工作 503 `layout_disabled`，同键精确重放保持可用；status 读取与既有结果永久可用（父 Issue disabled-mode 语义的请求侧半边；处理侧见 layout_job 表文档）。

## 验证

```bash
# 合同检查（additive 判断已审查）
MISTAKE_HTTP_ALLOW_ADDITIVE=true python3 tests/contracts/check_mistake_http.py --baseline-ref origin/master
python3 tests/contracts/test_mistake_http.py

# Mistake 解决方案构建与测试（含 LayoutHttpContractTests）
dotnet build src/services/ruoyu.mistake/src/Ruoyu.Study.Mistake.sln --configuration Release
dotnet test src/services/ruoyu.mistake/src/Ruoyu.Study.Mistake.sln --configuration Release

# 真实 PG provider wire 测试（fixture 对每个 JSON 响应做合同校验）
dotnet test tests/integration/src/Ruoyu.Study.Mistake.Provider.Tests/Ruoyu.Study.Mistake.Provider.Tests.csproj \
  --configuration Release --filter FullyQualifiedName~LayoutJobHttp
```

provider 场景覆盖：重放/冲突/独立 revision/显式结果、status 的每图状态与 validated regions 及凭据值不出现、disabled 模式三分行为、兼容/strict/resource 模式下的认证与角色拒绝。已部署 StructaDoc 端点的可复现冒烟见 [StructaDoc 集成](../Integration/StructaDoc.md#已部署端点冒烟)。

## 边界

不新增 Portal 或共享客户端消费者，不新增终端用户确认步骤或 UI；不部署本地 worker，不启用任何共享环境 rollout 开关制造输入。无 Portal 消费方时这些操作仅供集成与受控使用。
