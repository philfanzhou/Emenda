# Mistake 对象存储集成

Mistake 直连 `IOssService`；生产/Local 使用共享 `S3OssService`，显式 `USE_LOCAL_OSS=1` 才使用开发文件替身。S3 内部 endpoint 与公开签名地址职责见[共享服务](../../../../../ruoyu.common/docs/shared-services.md)，独立 Local 六项配置见 [LocalConfiguration](./LocalConfiguration.md)。图片与练习的数据库主责在 Mistake，跨域 student 引用不建立外库 FK。

## 两条实际路径

[SourceIntake](../../modules/SourceIntake.md) 保留原 manifest、SourceRegions 与来源对象，不执行旧迁移或 Student 移除。[SourceTargets](../../modules/SourceTargets.md) 只按已提交 group 的实际引用保留来源并复制到每尝试独立键，实际 Download 校验长度/SHA256后发布一次不可变 Verified 目标。所有 pending/unknown/verified 候选与选定目标持续保留；本服务没有候选清理、SourceReleased 或完整 handoff。

旧未受管 `UploadPollingWorker` 与 `CompleteReview` 仍直连 OSS 执行 `uploads/`→`mistakes/` 的前缀转换、Copy→Delete→业务路径更新；reject 反向复制并删除条目，然后经 Student HTTP 创建退回记录。它们没有跨 DB/S3 原子性或持久恢复保证，不通过已移除的 Student gRPC。已受管来源在副作用前拒绝旧 mover。

`mistakes/source-targets/` 与对应前缀换算输入 `uploads/source-targets/` 永久 reserved，三个旧 mover 的源及计算目标、item/practice/source 写边界均拒绝。不能以另一个 legacy item 或 practice 引用候选/目标，绕过来源关系后再移动、覆盖或删除它。

## 签名读取

legacy 未受管路径沿现有 `mistakes/`、size 与缩略图规则；是否启用兼容窗口由部署身份配置控制。受管原图及 Verified 选定目标始终要求 JWT 身份与真实 item 的 student 归属/Staff审核/Admin授权，只读 full，签名前 Download 核验原长度/SHA256。关闭 source/target/strict/resource 开关不能使受管 namespace 匿名；未知、候选、失配或伪造引用不签名。learner 归属依赖不可用返回 503。

## Storage Audit 边界

当前 Mistake **未实现** `GetAllReferencedImagePaths`。不得把旧文档中的该名称当作可用 API，也不得仅凭 item.source_regions/practice.image_paths 推断新增 namespace 的全部保护引用；source manifest、所有尝试与选定映射仍须保留。外部 Ruoyu.Admin 的完整引用/GC 合同属于后继任务，当前不能自动删除 source-targets 或来源 pins。部署者负责阻止独立外部 writer/GC 触碰它们；本服务凭据边界不能限制其他凭据持有者。

专项验证见 [SourceTargets](../../modules/SourceTargets.md#配置数据库与验证)。legacy 路径规则测试与实际三 mover 无副作用拒绝测试在 Mistake Service/Domain 测试项目；它们不把旧 Copy→Delete 顺序称为数据安全或跨资源幂等保证。
