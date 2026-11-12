# 独立服务本地验收记录

## 范围与身份

- 目标：独立 Emenda 服务供新的可信后端调用方使用；专用数据库与私有 S3，不接管 Ruoyu.Study。
- 源码基线：`a0ddaa2` 加本次未提交修改。本记录不代表已提交、已发布或已部署生产。
- 实际矩阵开始：`2026-11-11T10:00:40.984876+00:00`；结束：`2026-11-11T10:01:13.623782+00:00`。
- 实际运行：Linux arm64，PostgreSQL 15.19，SeaweedFS 3.85，Microsoft.AspNetCore.App / Microsoft.NETCore.App 10.0.15。
- Host 由 Release 构建并框架依赖发布，复制到真实 .NET runtime 容器执行，不是 TestServer 或 InMemory 数据库。
- VL 是任务自有的确定性 OpenAI-compatible 协议 fixture；实际图片会解码/压缩，但本记录不证明外部模型质量或供应商可用性。

实际参与验收的发布程序集 SHA-256：

| 程序集 | SHA-256 |
|---|---|
| `Emenda.Host.dll` | `4f7c5cc4fc390f5a50b347c6ae8b6a3d665927166ea488165107365f65f336c8` |
| `Emenda.Database.dll` | `d009bc972ddf7c35f09d8293cd2a626960df45dbff3a1096116fa9a2077881cf` |
| `Emenda.Service.dll` | `f5bcc80ff6341fe2e00b2830b7d32e1d512fc5fca6fccd6d2765be9bdaa7e724` |

## 自动检查

| 检查 | 观察结果 |
|---|---|
| Release solution build 与 Host publish | 通过；最终编译无新增警告 |
| 非 PostgreSQL Service 回归 | 475 项通过；此为首轮完整 portable 运行，不计未运行的 PostgreSQL 测试 |
| 凭据、管理 API、认证及模型一致性专项 | 后续修复后 43 项通过；与上一行有重叠，不累加 |
| 产品上传、真实 Host、模型及图片迁移专项 | 真实 PostgreSQL 17 项通过 |
| 历史 schema 收敛、guard、升级与只读启动 | 真实 PostgreSQL 179 项通过 |
| 显式凭据 bootstrap | 首次退出 0，重复初始化退出 2；原文件不变、Unix 权限 0600 |
| 脚本及产品文档 | 两个 shell 入口语法通过，Python AST 解析通过，权威产品文档无失效本地链接 |

schema snapshot 的 pending-model-change 在修复前被现有空库迁移测试实际捕获，修复为冻结完整列/类型/主键元数据，没有关闭 EF 检查。媒体类型幂等断言也实际观察到红灯，再加入规范化媒体类型绑定后转绿。

## 实际 Host + PostgreSQL + 私有 S3 矩阵

`script/verification/standalone.py run` 完整返回 0，15 项全部通过：

- `empty_database_own_startup_liveness_readiness`：通过。
- `missing_caller_key_401`：通过。
- `published_http_contract`：通过。
- `real_png_batch_upload_student_id_36`：通过。
- `student_id_37_rejected`：通过。
- `private_real_s3_signed_reads`：通过。
- `same_key_identical_replay`：通过。
- `same_key_different_student_or_content_409`：通过。
- `source_key_scoped_to_trusted_backend_caller`：通过。
- `six_concurrent_identical_requests`：通过。
- `review_and_practice_existing_http_contract`：通过。
- `host_restart_persisted_identity_and_practice`：通过。
- `real_s3_outage_same_key_same_content_recovery`：通过。
- `analysis_failure_same_key_same_content_recovery`：通过。
- `partial_batch_failure_not_success_and_same_key_recovery`：通过。

第二分组故障通过任务自有数据库中的临时 trigger 注入；失败前后 item 与 process_log 数量不变，恢复后完整提交且重放不重复。S3 故障通过停止本任务 S3 容器产生；不是把失败响应替换成成功。签名 GET 实际读取原图字节，去掉签名后拒绝匿名读取。

准备阶段发现并修复了隔离环境端口映射、VL fixture 的 HTTP chunked framing 及默认 S3 卷规格耗尽小 VM 磁盘的问题。失败轮次未计为通过；上述最终矩阵使用修复后的新 fixture 和全新空库运行。

## 资源清理

两轮失败环境与最终通过环境的自有容器、网络、卷，以及本任务 Colima profile 均已删除；临时发布目录、凭据和 fixture 已清理。默认 Colima profile 仍为 Stopped，Docker active context 仍为 default。仓库保留本记录与可重复验收入口，不保留临时秘密。

## 未执行与边界

- 未构建/推送本次产品 Dockerfile 镜像或创建 GitHub Release；实际运行使用发布输出加官方 runtime 容器。
- 未运行 git-archive 最小源码发布预演；该入口只导出已提交的 commit，本次修改尚未提交。
- 未执行真实 SignaCore hosted login、外部 VL 模型或旧平台切换；不是这些能力的验收记录。
- 未运行全部继承的 provider、S3、备份恢复与平台 E2E 测试；不得将上面的专项数相加后称为全套通过。
- `studentId` 公开上限为 36。新 sourceId 为 opaque 值；旧上传没有可信摘要，不能自动迁成新的 caller-scoped 幂等回执。
- 迁移 `20261111000000_AddProductIntakeRequests` 在 Own 模式自动应用；存在请求记录时拒绝回退删除该表。

复现步骤见 [StandaloneVerification](StandaloneVerification.md)，部署及恢复契约见 [StandaloneDeployment](StandaloneDeployment.md)。本记录不包含临时连接串、密钥、Cookie、Host 原始日志或活签名 URL。
