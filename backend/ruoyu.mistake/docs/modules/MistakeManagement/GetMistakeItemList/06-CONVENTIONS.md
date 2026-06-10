# 错题列表查询 — 约定与规范 (CONVENTIONS)

## 命名约定

- gRPC 方法: `GetMistakeItemList` (PascalCase)
- 请求消息: `GetMistakeItemListRequest`
- 响应消息: `MistakeItemPageResult`
- 领域服务方法: `GetMistakeItemListAsync`
- 仓储方法: `GetListAsync`

## 分页规范

- 分页参数名统一为 `page` 和 `size`
- 页码从 1 开始
- 默认每页 10 条，最大 100 条
- 返回 `PageMeta` 包含 `page`, `size`, `totalCount`, `totalPages`
- `totalPages` 计算公式: `(totalCount + size - 1) / size`

## 筛选规范

- 可选筛选参数使用 `>0` 判断是否生效
- 值为 0 时表示"不筛选"
- 固定按 `created_at` 降序排列

## 日志规范

- 本功能无专门的日志输出

## 安全规范

- studentId 为必传参数，但当前不做权限校验
- 不做数据脱敏

## 错误消息格式

| 场景 | 消息文本 | 返回方式 |
|------|----------|----------|
| 数据库异常 | 由 gRPC 框架自动返回 Internal 错误 | RpcException(StatusCode.Internal) |
