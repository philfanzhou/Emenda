# Student HTTP 集成

Mistake 通过 HTTP 主动访问 Student，获取已分类的 Processing 上传记录，通知 Student 移除已处理图片，并在错题审核退回后创建 Returned 上传记录。`student_id` 是跨上下文引用；Mistake 数据库不对 Student 数据库建立外键。

## 边界与调用路径

Mistake Service 只依赖自有的 `IUploadSourceGateway` 与本地模型。Host 的 `StudentUploadSourceGateway` 将它映射到共享 `IStudentHttpClient`。`UploadPollingWorker` 和审核端点使用同一个 scoped adapter 注册。地址由 Student 能力开关（`Mistake:Student:Enabled`，Local 缺省 `false`、Platform 缺省 `true`）决定：Local 启用能力时只接受显式 `Mistake:Local:Student:Url`，不从 `StudentService:Url`、`StudentService:BaseUrl`、`StudentService:GrpcUrl` 或 `http://localhost:5005` 回退；Platform 启用能力时仍按该顺序选择既有地址；能力禁用时客户端注册到保留的不可解析占位地址，本进程不会向 Student 发出任何请求。URL、JSON 和 HTTP 行为仍由现有共享客户端控制。配置细节见[部署说明](../development/Deployment.md)的“Student 能力开关与退回失败关闭”。

| Mistake 调用方 | 自有端口操作 | Student HTTP 客户端操作 |
|---|---|---|
| `UploadPollingWorker` | `GetProcessingUploadsAsync` | `ScanProcessingUploadRecordsAsync` |
| `UploadPollingWorker` | `RemoveProcessedImagesAsync` | `RemoveProcessedImagesAsync` |
| `MistakeHttpEndpoints` 审核退回 | `CreateReturnedRecordAsync` | `CreateReturnedRecordAsync` |

扫描映射保留记录 ID、Student ID、图片路径与类型、`hasMore` 和下一游标。worker 仅在成功读取一页并处理后使用下一游标；页间来源状态改变或删除不会使剩余项因 offset 左移而漏取。真正空页正常结束；HTTP 4xx/5xx、业务失败、网络故障、超时和坏 JSON 均作为故障停止本轮，下轮从新界限重试。调用方取消直接退出，不计为成功。移除结果保留成功标志、删除标志、剩余数量、错误消息与 `UnknownRecord` 分类（见下节）；退回操作保留 Student ID、图片路径和类型、备注与退回原因。adapter 复制可变列表，原样传递取消令牌及异常；不增加重试或缓存。

## 移除回调三态语义（IKJEY6）

`UploadPollingWorker` 的移除回调（Student `POST /api/uploads/{recordId}/remove-processed`）结果分三类，Worker 按类记录日志：

- 成功（含墓碑重放）：`RecordDeleted=true` 既可能是本次完整移除，也可能是 Student 依据删除墓碑 `upload_record_removal_tombstone` 对已完成的移除给出的幂等确认——两者都是终态确认，Worker 记 Info，不把重放成功当作新的移除动作。
- 未知记录（`unknown_record`）：记录不存在且无墓碑证明此前已完成。这是已迁移对象与 Student 记录之间的数据不一致，重试无法解决——Worker 记 Warning（含需人工核查来源记录语义），不重试；自动对账与修复入口属 IKJ0IO，本项不引入。
- 其他失败：保持既有 Warning 行为；传输异常保持既有 Error 行为。

共享 `StudentHttpClient.RemoveProcessedImagesAsync` 透传 Student 响应的 `errorKind` 字段；`IUploadSourceGateway` 将其映射为 `RemoveProcessedUploadImagesResult.UnknownRecord`。存量已删除记录（墓碑引入前）重放按 `unknown_record` 返回，历史缺口由 IKJ0IO 对账处理。

## 当前失败语义

能力开关（`Mistake:Student:Enabled=false`）是第一层失败关闭：`UploadPollingWorker:Enabled=true` 与之矛盾并在启动前报错；`POST /api/mistakes/{id}/review` 请求 `ReviewStatus=Rejected` 时在调用领域服务、OSS 或 Student 之前返回 HTTP 503 与稳定响应（`Student 集成未启用，退回审核不可用`），不产生任何部分副作用，`ReviewStatus=Confirmed` 路径不受影响。

旧共享 `GetProcessingUploadRecordsAsync` 仍可能将查询故障降为空页，供尚未迁移的调用方兼容使用；Mistake worker 使用新的严格扫描方法。创建退回记录的失败仍可能被吞掉；移除回调已按上节三态语义分类。因此本切片不保证写回调幂等或跨数据库与 OSS 的原子性，其余保证由 [迁出总览](https://gitee.com/philfanzhou/Ruoyu.Study/issues/IKIZXV) 的后续任务负责。先发布 Student 新端点和迁移，再发布本版 Mistake；反序部署时 Mistake 读取会显式失败，不回退到旧 offset 分页。

## 验证

adapter、HTTP 失败矩阵、移除回调 `errorKind` 透传（`RemoveProcessedImagesHttpClientTests`）、Worker 移除回调分类日志（`UploadPollingNotifyTests`）、worker 游标与 Student 能力开关（含 503 失败关闭、矛盾配置与显式地址校验）测试位于 `src/Tests/Ruoyu.Study.Mistake.Service.Tests/`。在 `src/services/ruoyu.mistake/src` 运行 `dotnet build Ruoyu.Study.Mistake.sln --configuration Release` 和 `dotnet test Ruoyu.Study.Mistake.sln --configuration Release`。Student 的隔离 PostgreSQL 扫描测试需显式提供 `STUDENT_SCAN_TEST_CONNECTION`，只在临时数据库执行。
