# Student HTTP 集成

Mistake 通过 HTTP 主动访问 Student，获取已分类的 Processing 上传记录，通知 Student 移除已处理图片，并在错题审核退回后创建 Returned 上传记录。`student_id` 是跨上下文引用；Mistake 数据库不对 Student 数据库建立外键。

## 边界与调用路径

Mistake Service 只依赖自有的 `IUploadSourceGateway` 与本地模型。Host 的 `StudentUploadSourceGateway` 将它映射到共享 `IStudentHttpClient`。`UploadPollingWorker` 和审核端点使用同一个 scoped adapter 注册。Student 地址仍由 Host 按 `StudentService:Url`、`StudentService:BaseUrl`、`StudentService:GrpcUrl`、`http://localhost:5005` 的顺序选择；URL、JSON 和 HTTP 行为仍由现有共享客户端控制。

| Mistake 调用方 | 自有端口操作 | Student HTTP 客户端操作 |
|---|---|---|
| `UploadPollingWorker` | `GetProcessingUploadsAsync` | `ScanProcessingUploadRecordsAsync` |
| `UploadPollingWorker` | `RemoveProcessedImagesAsync` | `RemoveProcessedImagesAsync` |
| `MistakeHttpEndpoints` 审核退回 | `CreateReturnedRecordAsync` | `CreateReturnedRecordAsync` |

扫描映射保留记录 ID、Student ID、图片路径与类型、`hasMore` 和下一游标。worker 仅在成功读取一页并处理后使用下一游标；页间来源状态改变或删除不会使剩余项因 offset 左移而漏取。真正空页正常结束；HTTP 4xx/5xx、业务失败、网络故障、超时和坏 JSON 均作为故障停止本轮，下轮从新界限重试。调用方取消直接退出，不计为成功。移除结果保留成功标志、删除标志、剩余数量和错误消息；退回操作保留 Student ID、图片路径和类型、备注与退回原因。adapter 复制可变列表，原样传递取消令牌及异常；不增加重试或缓存。

## 当前失败语义

旧共享 `GetProcessingUploadRecordsAsync` 仍可能将查询故障降为空页，供尚未迁移的调用方兼容使用；Mistake worker 使用新的严格扫描方法。创建退回记录的失败仍可能被吞掉；移除回调失败返回 `Success=false`。Student 的移除操作可能删除 OSS 对象，重复移除不存在的记录会失败。因此本切片不保证写回调幂等或跨数据库与 OSS 的原子性。这些保证由 [迁出总览](https://gitee.com/philfanzhou/Ruoyu.Study/issues/IKIZXV) 的后续任务负责。先发布 Student 新端点和迁移，再发布本版 Mistake；反序部署时 Mistake 读取会显式失败，不回退到旧 offset 分页。

## 验证

adapter、HTTP 失败矩阵与 worker 游标测试位于 `src/Tests/Ruoyu.Study.Mistake.Service.Tests/`。在 `src/services/ruoyu.mistake/src` 运行 `dotnet build Ruoyu.Study.Mistake.sln --configuration Release` 和 `dotnet test Ruoyu.Study.Mistake.sln --configuration Release`。Student 的隔离 PostgreSQL 扫描测试需显式提供 `STUDENT_SCAN_TEST_CONNECTION`，只在临时数据库执行。
