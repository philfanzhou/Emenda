# Mistake 与 Portals 集成 — 约定

## API 路由命名约定

### 路由前缀

| Portal | 前缀 | 说明 |
|--------|------|------|
| Ruoyu.Admin | `api/admin/mistakes` | 管理端，复数名词 |
| teacher_portal | `api/mistake` | 教师端，单数名词 |
| user_portal | `api/mistake` | 学生端，单数名词 |

### HTTP 方法语义

| 操作 | HTTP 方法 | 示例 |
|------|-----------|------|
| 查询列表 | GET | GET /api/mistake/list |
| 查询详情 | GET | GET /api/mistake/{id} |
| 创建/执行操作 | POST | POST /api/mistake/review-item |
| 更新 | PUT | PUT /api/admin/mistakes/{id} |
| 删除 | DELETE | DELETE /api/mistake/{id} |

### 特殊路由

| 路由 | Portal | 说明 |
|------|--------|------|
| `by-upload/{uploadId}` | admin | 按上传记录查询，使用连字符分隔 |
| `migrate-images/{id}` | admin | 手动迁移图片，使用连字符分隔 |
| `pending-reviews` | teacher | 待审核列表，使用连字符分隔 |
| `review-detail/{id}` | teacher | 审核详情，使用连字符分隔 |
| `review-item` | teacher | 审核单条，使用连字符分隔 |
| `batch-review` | teacher | 批量审核，使用连字符分隔 |
| `add-item` | teacher | 手动添加，使用连字符分隔 |
| `complete-review` | teacher | 完成审核，使用连字符分隔 |
| `pending-items` | teacher | 待审核条目，使用连字符分隔 |
| `{id}/return` | teacher | 退回错题，使用连字符分隔 |

### 命名规则

- 多词路由使用 **kebab-case**（连字符分隔）：`pending-reviews`、`batch-review`
- 路由参数使用 **camelCase**：`{sourceUploadId}`
- Query 参数使用 **camelCase**：`studentId`、`pageSize`

## 错误码映射约定

### gRPC → HTTP 状态码映射

| gRPC StatusCode | HTTP 状态码 | 说明 |
|-----------------|-------------|------|
| OK | 200 | 成功 |
| NotFound | 200 + Success=false | 资源不存在，业务层处理为"不存在或无权操作" |
| InvalidArgument | 400 | 参数校验失败 |
| Unavailable | 502 | gRPC 服务不可用 |
| Internal | 500 | 服务内部错误 |
| DeadlineExceeded | 504 | 请求超时 |

### 业务错误响应格式

各 Portal 的错误响应格式略有不同：

**Ruoyu.Admin**：
```json
{ "message": "Failed to get mistake items" }
```
使用 `ErrorResponse` 对象，HTTP 状态码表示错误类型。

**teacher_portal**：
```json
{ "success": false, "message": "未选择任何错题" }
```
HTTP 200 + 业务字段 `success=false`，或 HTTP 502（gRPC 不可用）。

**user_portal**：
```json
{ "success": false, "message": "该错题已确认，无法删除" }
```
HTTP 200 + 业务字段 `Success=false`，或 HTTP 502（gRPC 不可用）。

### 错误处理原则

1. **gRPC RpcException**：捕获后映射为对应的 HTTP 状态码
2. **业务校验失败**：返回 200 + `success=false` + 描述性 message
3. **参数缺失**：返回 400 Bad Request
4. **gRPC 不可用**：返回 502 Bad Gateway
5. **未预期异常**：返回 500 + 日志记录

## 权限校验约定

### 分层原则

```
┌─────────────────────────────────────┐
│  Portal 层（权限校验）               │
│  - 认证：通过 Identity 服务完成      │
│  - 授权：各 Portal 自行实现          │
│  - 过滤：在 Portal 层过滤数据        │
├─────────────────────────────────────┤
│  gRPC 传输层                        │
│  - 无认证/授权信息传递               │
│  - Mistake 服务信任 Portal 层       │
├─────────────────────────────────────┤
│  Mistake 服务层（无权限校验）        │
│  - 不做权限校验                     │
│  - 执行请求的操作，返回数据          │
└─────────────────────────────────────┘
```

### 各 Portal 权限实现

| Portal | 策略 | 实现方式 |
|--------|------|----------|
| Ruoyu.Admin | 无过滤 | 管理员可查看所有数据 |
| teacher_portal | 学科过滤 | `GetTeacherSubjects()` → gRPC 请求中携带 Subjects → 内存二次过滤 |
| user_portal | 学生ID过滤 | 必填 `studentId` 参数 + `ReviewStatus=Confirmed` 固定 |

### teacher_portal 学科过滤约定

1. **获取教师学科**：从 JWT 的 `ClaimTypes.NameIdentifier` 或 `sub` Claim 获取 userId，调用 `TeacherDbService.GetSubjectsByUserId(userId)`
2. **无学科参数**：使用教师全部学科作为 gRPC 请求的 Subjects 过滤
3. **有学科参数**：取请求学科 ∩ 教师学科，交集为空时返回空结果
4. **内存过滤场景**：`GetMistakeItemList` 不支持多学科参数时，使用 `Subject=-1` + 内存过滤

### user_portal 状态过滤约定

1. **查询**：`ReviewStatus` 固定为 `Confirmed`，学生只能看到已确认的错题
2. **删除**：先查询状态，`Confirmed` 状态不允许删除
3. **studentId**：必填参数，由前端传入；user_portal 校验其属于当前账户，get / delete 校验错题所属学生，否则返回 403（见 [User Portal 学生归属校验](../../../../../user_portal/docs/api.md#学生归属校验)）

### 认证约定

- 所有 Portal 通过 Identity 服务完成认证
- JWT Token 中的 `ClaimTypes.NameIdentifier` 或 `sub` 作为用户标识
- teacher_portal 从 JWT 提取 reviewerId 用于审核记录
- Mistake 服务不接收任何认证信息，完全信任 Portal 层

## 分页参数约定

| Portal | 默认页码 | 默认每页条数 | 最大每页条数 |
|--------|----------|-------------|-------------|
| Ruoyu.Admin | 1 | 20 | 100 |
| teacher_portal (pending-reviews) | 1 | 10 | 50 |
| teacher_portal (pending-items) | 1 | 20 | 100 |
| teacher_portal (list) | 1 | 10 | 50 |
| user_portal | 1 | 10 | 50 |

### 参数名约定

| 含义 | Ruoyu.Admin | teacher_portal | user_portal |
|------|-------------|----------------|-------------|
| 页码 | page | page | page |
| 每页条数 | size | pageSize | pageSize |

### 边界处理

- `page <= 0` → 修正为 1
- `pageSize <= 0` → 修正为默认值
- `pageSize > 最大值` → 修正为最大值
