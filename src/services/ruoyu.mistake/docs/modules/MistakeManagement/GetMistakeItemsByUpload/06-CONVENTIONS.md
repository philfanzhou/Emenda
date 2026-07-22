# 按上传记录查询错题 — 约定与规范 (CONVENTIONS)

## 命名约定

- gRPC 方法: `GetMistakeItemsByUpload` (PascalCase)
- 请求消息: `GetMistakeItemsByUploadRequest`
- 响应消息: `MistakeItemListResponse`
- 领域服务方法: `GetMistakeItemsByUploadAsync`
- 仓储方法: `GetBySourceUploadIdAsync`

## 排序规范

- 固定按 `created_at` 升序排列
- 排序在客户端执行（LINQ `OrderBy`）

## 日志规范

- 本功能无专门的日志输出

## 安全规范

- sourceUploadId 为外部标识字符串，不做格式校验
- 不做权限校验，任何 sourceUploadId 均可查询

## 错误消息格式

| 场景 | 消息文本 | 返回方式 |
|------|----------|----------|
| 数据库异常 | 由 gRPC 框架自动返回 Internal 错误 | RpcException(StatusCode.Internal) |
