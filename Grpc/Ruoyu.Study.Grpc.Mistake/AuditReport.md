# Ruoyu.Study.Grpc.Mistake 审计报告

## 1. 严重问题 (Critical Issues)

### 1.1 违反项目核心规则：解决方案格式错误
* **发现**：项目中使用了新型的 `.slnx` 格式 (`Ruoyu.Study.Mistake.slnx`)，而不是传统的 `.sln` 格式。
* **影响**：直接违反了项目的核心开发规则（“始终使用 .sln 解决方案格式，绝对不要生成或使用新型的 .slnx 格式”）。

### 1.2 核心业务缺失：MinIO 对象存储未实现
* **发现**：`README.md` 与 `docs/database.md` 中明确规定错题图片、重答图片等需要存储在 MinIO 中，格式为 `m_{guid}_{index}.jpg`。但是在 `MistakeDomainService` 和 `MistakeServiceImpl` 中，图片上传接口（如 `UploadMistake` 和 `AddMistakePractice`）完全忽略了请求中传入的 `images` 字节流，也没有引入任何 MinIO SDK 依赖。
* **影响**：核心的图片上传与存储功能完全缺失，学生上传的错题原图数据会被系统直接丢弃。

### 1.3 核心业务缺失：处理日志 (Process Log) 未记录
* **发现**：`docs/database.md` 规定需要“记录错题被分析、练习、掌握等每一个生命周期节点的行为”。但在 `MistakeDomainService` 的状态流转方法中（如新建错题、标记处理、新增练习等），完全没有向 `ProcessLogs` 表中插入对应审计日志的逻辑。
* **影响**：错题处理的流转日志功能失效，无法追溯错题状态变更历史。

### 1.4 gRPC 接口未实现
* **发现**：`mistake.proto` 中定义了 `AnalyzeMistakeType`, `LogMistakeProcess`, `GetMistakeProcessHistory` 三个重要的错题分析与日志查询接口，但 `MistakeServiceImpl` 并没有重写并实现这些方法。
* **影响**：当客户端调用这些接口时，将直接抛出 `Unimplemented` 异常，导致服务不可用。

---

## 2. 潜在隐患与代码异味 (Code Smells)

### 2.1 硬编码魔法值 (Magic Numbers)
* **发现**：在 `MistakeDomainService.cs` 中，`Status` 字段大量使用了硬编码数字（例如 `mistake.Status = 1; // PENDING` 和 `if (mistake.Status != 1)`）。
* **建议**：应当使用强类型的 Enum（例如对应 Proto 生成的 `MistakeStatus`，或在 Domain 层定义专属枚举）来进行状态赋值和判断，提高代码可读性和健壮性。

### 2.2 分页查询未进行防御性校验
* **发现**：`MistakeDomainService` 中的 `GetMistakeListAsync` 方法直接使用了 `Skip((page - 1) * size)`。
* **影响**：如果客户端传入 `page <= 0` 或 `size < 0`，将抛出 `ArgumentOutOfRangeException`，导致查询接口崩溃。
* **建议**：在 `MistakeServiceImpl` 或领域服务层入口处，对 `page` 和 `size` 增加大于 0 的防御性验证。

### 2.3 浏览器经 Nginx 转发场景下的文件上传与性能隐患
* **发现**：`mistake.proto` 中的 `UploadMistakeRequest` 直接使用 `repeated bytes images = 4;` 传输原图。
* **影响**：既然架构设计是由前端页面通过 Nginx 转发请求到后端，前端通常只能发起 HTTP/1.1 或普通 HTTP/2 请求（通过 gRPC-Web 或 gRPC-JSON Transcoding 转换）。在这种情况下，`bytes` 数组会被 Base64 编码，导致载荷体积膨胀 33% 以上。这不仅极易突破 gRPC 默认的 4MB 消息限制，还会给前端、Nginx 和后端的内存带来沉重的序列化/反序列化负担。此外，浏览器端本身也不支持 gRPC 的客户端流式传输（Client Streaming）。
* **建议**：
  1. **暴露独立的 HTTP 接口（推荐）**：在同一个 ASP.NET Core 服务中，额外增加一个标准的 RESTful 接口（如 `[HttpPost("api/mistakes/upload")]`），使用 `multipart/form-data` 接收图片并流式上传至 MinIO。gRPC 接口退化为仅接收业务数据和图片 URL。
  2. **MinIO 预签名 URL 直传**：前端先通过 gRPC（经 Nginx 转发）向服务端申请 MinIO 的预签名上传 URL，前端直接将文件 PUT 到该 URL（MinIO 也可以配置在 Nginx 代理之下），完成后再调 gRPC 提交业务数据。

---

## 3. 架构与设计优化建议 (Improvements)

### 3.1 Entity Framework Core 字段映射优化
* **发现**：`MistakeEntity` 和 `MistakePracticeEntity` 中的 `ImagePaths` 字段，采用了一个额外的 `[NotMapped]` 包装属性加上 `System.Text.Json` 序列化来实现 `List<string>` 的读写。
* **建议**：在 EF Core 8.0 中，更优雅、现代的做法是在 `MistakeDbContext.OnModelCreating` 中使用 `.HasConversion()` 注册值转换器（Value Converter），这样实体可以直接暴露出 `List<string>` 类型的属性，无需定义冗余的包装字段。

### 3.2 数据库主键类型优化
* **发现**：在 `Entity` 类与 `docs/database.md` 规范中，所有表的主键 `id` 均被设计为 `VARCHAR(36)`，并在 C# 代码中采用 `Guid.NewGuid().ToString()` 生成。
* **建议**：在 PostgreSQL 数据库中，原生支持 `uuid` 数据类型，其在查询性能、索引构建和存储空间效率上均远高于 `VARCHAR(36)`。如果条件允许，建议将主键字段类型优化为原生的 UUID。