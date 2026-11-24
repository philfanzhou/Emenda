# 集成索引

- [Conversation collection](./ConversationCollection.md): default-off product authentication, current Portal authorization callback, durable receipts and independent snapshots.

| 外部系统 | 交互点入口 |
|----------|------------|
| AI Tutor | [配套实现](../../integrations/ai-tutor/README.md)、[Conversation collection](./ConversationCollection.md) |
| Oss | [集成概览](./Oss/Oss.md) |
| Storage | [Storage](./Storage.md) |
| StructaDoc | [StructaDoc](./StructaDoc.md) |

- [版本化机器 HTTP 合同](./MachineHttpContract.md)：唯一 OpenAPI3.1 artifact、发布 hash/ETag 与 provider/consumer/兼容差异验证。

## 仅图片上传：Portal 学生断言（Emenda #33）

`POST /api/intake/extractions/images` 是独立的服务端入口。multipart 正文只包含图片；原 `POST /api/intake/extractions` 的凭据和 `studentId`、`grade`、`sourceKey` 表单字段契约不变。本文定义接收方契约，不表示 Portal 已迁移。

Portal 后端应从已认证会话解析当前学生、确认用户有权为该学生上传，并签发断言；不得直接签署浏览器提交的学生 ID。通过 HTTPS 同时发送原有 `X-Emenda-Api-Key` 和单个 `X-Emenda-Student-Assertion`，后者的值为紧凑 JWT，不加 `Bearer` 前缀。私钥和 API key 只留在服务端，勿记录断言。Emenda 仅从验证成功的 `sub` 取得学生 ID，不读取表单、查询或其他请求头作为身份来源。

JWT JOSE 头只包含 `alg: RS256` 和 `typ: emenda-student-assertion+jwt`。JWT 最长 4096 字符。必需的签名 claims：

| Claim | 值 |
|-------|----|
| `iss`, `aud` | 与该调用方在 Emenda 中配置的字符串完全一致，`aud` 不接受数组 |
| `caller` | 与 API key 中间件认证出的调用方名称完全一致 |
| `purpose`, `route` | 固定为 `worksheet-images-upload`、`/api/intake/extractions/images` |
| `sub` | Portal 的规范学生 ID，格式 `[A-Za-z0-9][A-Za-z0-9_-]{0,35}`；区分大小写，不作归一化 |
| `iat`, `exp` | 整数 Unix 秒；`iat <= now < exp` 且 `exp - iat <= 120`，无时钟宽限。可选 `nbf` 若存在也必须为整数 Unix 秒，且不得晚于当前时间 |

在 `Emenda:Security:PortalStudentAssertions` 列表中为每个获准调用方配置唯一条目，字段为 `CallerName`、`Issuer`、`Audience`、`PublicKeyPem`。`PublicKeyPem` 是至少 2048 位的 RSA SubjectPublicKeyInfo PEM（`BEGIN PUBLIC KEY`），不是私钥、证书或远程密钥地址。现有 API key 中间件的响应不变；即使启用了匿名开发模式，本入口没有中间件认证出的调用方也返回 `401 caller_required`。调用方已认证但信任配置缺失、不完整或断言校验失败时统一返回 `403 student_assertion_invalid`，不回显细节。
