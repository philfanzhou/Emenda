# 错误处理规范

## gRPC 状态码使用规则

gRPC 服务实现中必须使用标准的状态码，不得自定义状态码：

| StatusCode | 使用场景 | 示例 |
|------------|----------|------|
| `InvalidArgument` | 请求参数验证失败 | ID 格式无效、必填字段为空 |
| `NotFound` | 请求的资源不存在 | 学生不存在、错题不存在 |
| `AlreadyExists` | 资源已存在（创建时冲突） | 重复提交 |
| `FailedPrecondition` | 业务前置条件不满足 | 审核非待审核状态的错题 |
| `PermissionDenied` | 权限不足 | 越权访问 |
| `Unauthenticated` | 认证失败 | Token 无效或过期 |
| `ResourceExhausted` | 资源限制 | 限流、配额耗尽 |
| `Internal` | 服务内部错误 | 数据库异常、未预期的错误 |
| `Unavailable` | 服务不可用 | 依赖服务宕机 |

## 错误信息规范

1. gRPC 错误信息使用中文，因为调用方（REST API 控制器）需要将信息展示给终端用户
2. 错误信息应简洁明确，不包含技术细节
3. 同一类错误在各服务中使用相同的措辞

## 全局异常拦截器

所有 gRPC 服务必须注册全局异常拦截器：

- `RpcException` 直接重新抛出，不做包装
- 领域异常映射为 `InvalidArgument` 或 `FailedPrecondition`
- 其他未捕获异常统一返回 `Internal`，错误信息脱敏

## 参数验证

参数验证应在 gRPC 服务方法入口处进行，尽早返回错误。

## 日志规范

### Serilog + Loki 日志系统

Mistake 使用 Serilog 替代原生 Microsoft.Extensions.Logging，双写到 Console + Grafana Loki。接入方式与 Identity / DocLibrary 服务完全一致。

配置入口：`Program.cs` 中 `UseAgentSerilog("Ruoyu.Study.Mistake")`，读取 `appsettings.json` 中 `Serilog` 配置节。

Loki 地址统一通过配置键 `Loki:Uri` 注入，`Program.cs` 启动时读取并覆盖 `Serilog:WriteTo:1:Args:uri` 配置键：

| 配置键 | 来源 | 示例值 | 说明 |
|--------|------|--------|------|
| `Loki:Uri` | Consul `config/ruoyu/shared.json` | http://ruoyu-loki:3100 | 推荐由 Consul 共享配置提供 |
| `Loki:Uri`（fallback） | `appsettings.json` | http://localhost:3100 | Consul 不可达时的兜底地址 |

> **容错机制**：如果 `Loki:Uri` 未设置（Consul 和 appsettings 均未提供），Loki Sink 使用 appsettings.json 中 `Serilog:WriteTo:1:Args:uri` 的 fallback 地址 `http://ruoyu-loki:3100`。Loki 不可达时 Sink 异步重试，不影响服务启动。`start.sh` 不传 `LOKI_URI` 环境变量，Loki 地址完全由 Consul 提供。

### 结构化日志规则

- 使用结构化日志占位符，不要使用字符串插值
- 异常对象必须传入：使用 `LogError(ex, ...)` 而非 `LogError(ex.Message, ...)`
- RpcException 应记录状态码和详情
- 预期内的 NotFound 使用 Warning 级别
- 不记录密码、Token、手机号等敏感信息
