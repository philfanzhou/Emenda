# 错题条目查询 — 约定与规范 (CONVENTIONS)

## 命名约定

- gRPC 方法: `GetMistakeItem` (PascalCase)
- 请求消息: `IdRequest`（通用 ID 请求，跨 RPC 复用）
- 响应消息: `MistakeItemDto`
- 领域服务方法: `GetMistakeItemAsync`
- 仓储方法: `GetByIdAsync`

## 日志规范

- 本功能无专门的日志输出

## 安全规范

- ID 为 Guid 格式，gRPC 层使用 `Guid.Parse` 解析，格式错误时由框架抛出异常

## 错误消息格式

| 场景 | 消息文本 | 返回方式 |
|------|----------|----------|
| 错题不存在 | `错题条目不存在` | RpcException(StatusCode.NotFound) |
| ID 格式无效 | 由 Guid.Parse 抛出 | 框架异常 |
