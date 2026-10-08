# Mistake Local 模式的 OSS 配置

`Mistake:ConfigMode=Local` 正常运行时使用现有 `S3OssService`，但只从 `Mistake:Local:Oss:*` 读取 S3 配置。它不继承通用 `Oss:*`、仓库示例或 `OssOptions` 默认值。默认及显式 `Platform` 仍读取通用 `Oss:*`；其他服务不使用 Mistake 的 Local section。`USE_LOCAL_OSS=1` 是另一项既有开发用途开关，不是 `Mistake:ConfigMode` 的同义词。

| Local 配置键 | 环境变量后缀 | 要求 |
|---|---|---|
| `InternalEndpoint` | `InternalEndpoint` | 内部 S3 的 `host` 或 `host:port`，无协议或路径 |
| `InternalSecure` | `InternalSecure` | 显式 `true` 或 `false` |
| `AccessKey` | `AccessKey` | 非空、非仓库 mock 凭据 |
| `SecretKey` | `SecretKey` | 非空、非仓库 mock 凭据 |
| `BucketName` | `BucketName` | 非空的目标桶名 |
| `PublicBaseUrl` | `PublicBaseUrl` | 绝对 HTTP(S) URL，可含公开路径前缀，无凭据、查询或片段 |

环境变量完整名称为 `Mistake__Local__Oss__` 加表中的后缀。所有键必须显式提供；文件中的值可由环境变量逐键覆盖。示例文件应作为受保护的部署配置挂载，不提交真实凭据：

```json
{
  "Mistake": {
    "ConfigMode": "Local",
    "Local": {
      "Oss": {
        "InternalEndpoint": "<s3-host>:<s3-port>",
        "InternalSecure": false,
        "AccessKey": "<access-key>",
        "SecretKey": "<secret-key>",
        "BucketName": "<existing-bucket>",
        "PublicBaseUrl": "https://<public-host>/oss"
      }
    }
  }
}
```

内部请求只使用 `InternalEndpoint`；预签名 URL 使用 `PublicBaseUrl` 的 scheme、Host、端口和路径前缀。公开代理必须保留用于签名的 `Host`，去掉公开路径前缀后再转发到 S3。部署者负责代理 TLS、容器网络、桶和凭据；配置校验不探测网络或桶，也不代表 `GET /` 已完成依赖就绪检查。`--upgrade-empty-db` 和 `--upgrade-known-db` 两个兼容命令只执行数据库升级，不读取 Local OSS 配置；正常启动在数据库升级前就读取并校验这些配置。

正常启动在连接数据库前按缺失键或无效键失败，诊断不包含配置值。OSS 不可达、桶不存在或凭据错误属于后续对象请求故障，不会回退到通用 `Oss:*` 或生成平台公共地址。排查时先检查最终文件与环境注入、内部 endpoint 和桶，再检查公开代理的 Host 与路径剥离。回退配置时恢复上一个已验证的 Local section 并重启实例；每个实例在启动时独立读取最终值。已经签发的 URL 仍按原到期时间失效，密钥轮换须与 S3 和代理协调。

路径授权与对象迁移仍按现有 [接口规格](./Oss.md) 执行；Local 配置不扩大可访问前缀，也不改变 Student 接口或匿名入口的授权状态。
