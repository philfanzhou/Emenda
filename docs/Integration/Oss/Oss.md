# Mistake 对象存储集成

Emenda 通过 `IOssService` 直连一个私有 S3 兼容桶（`Oss:*` 配置，见[部署说明](../../development/StandaloneDeployment.md)），只读写本产品的 `mistakes/` 前缀。图片与练习的数据库主责在 Emenda，学生引用是调用方的不透明 ID，不建立外库 FK。

## 写入路径

- 自动提取把原图写入 `mistakes/extractions/`，回读校验后保留原图与每个修订版本的裁图；读取走 `/api/intake/extractions/{id}/sources/...` 与 `.../questions/.../images/...` 专用端点，通用签名端点拒绝该前缀。
- 对话采集把图片复制到按内容寻址的 `mistakes/conversation/{callerHash}/...`，回读校验哈希后才提交引用；只有所属调用方能签名读取。

本服务不再移动、复制或删除 `uploads/` 前缀下的对象（旧 Student 上传轮询、CompleteReview 迁移与受管来源目标/退回均已移除）。审核退回不改动图片。

## 签名读取

`POST /api/mistakes/presigned-url` 只接受 `mistakes/` 前缀，支持 full 与缩略图尺寸；对话图片只允许 full 且须为所属调用方的规范路径。返回的预签名 URL 是短期持有凭据，公开代理须把签名 Host 与对象路径路由到同一个桶。

## 对象清理边界

本服务不做物理清理，也不提供引用快照或存储审计。部署者负责阻止外部 writer/GC 触碰本桶对象；本服务凭据边界不能限制其他凭据持有者。
