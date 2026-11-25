# 错题图片显示尺寸（缩略图）

错题服务里所有界面上显示的图片使用同一套尺寸、同一种生成方式和同一条清理规则。代码中的唯一定义是 `Emenda.Common.Oss.ImageVariants`。

## 图片种类

| 种类 | 对象路径 | 生命周期 | 读取入口 |
|---|---|---|---|
| 旧错题图 / 练习图 | `mistakes/yyyy/...` | 随错题 | `POST /api/mistakes/presigned-url`、`GET /api/mistakes/{id}/images/{index}` |
| 切题原图 | `mistakes/extractions/{job}/originals/{source}.{jpg,png}` | 随任务长期保留；管理员/运维删除任务后回收 | `GET /api/intake/extractions/{id}/sources/{sourceId}` |
| 补图原图 | `mistakes/extractions/{job}/supplements/{s}/originals/...` | 接受后同原图；未接受的由回收队列清理 | 同上（接受后成为任务原图） |
| 当前错题裁图 | `mistakes/extractions/{job}/versions/{request}/{region}-{sha256}.png` | 只保留当前错题；纠偏、改判或删错题后回收；删除任务时，已确认错题的裁图保留归档，其余回收 | `GET .../questions/{questionId}/images/{version}/{index}`、删除任务后 `GET .../retained/{itemId}/images/{index}`、`GET /api/mistakes/{id}/images/{index}` |
| 对话证据图 | `mistakes/conversation/{callerHash}/...` | 随对话错题 | 签名接口（须为所属调用方）、`GET /api/mistakes/{id}/images/{index}` |

## 尺寸

| `size` | 长边 | 格式 | 用途 |
|---|---|---|---|
| `full`（默认；`large`、`original` 为别名） | 原始 | 存储的原格式 | 点开查看大图、框选编辑器 |
| `medium` | 800 px | JPEG | 详情主图、纠偏参照原图 |
| `small` | 400 px | JPEG | 列表卡片、题图卡片、胶片条 |
| `thumbnail` | 200 px | JPEG | 原图画廊胶片条、原图列表 |

其他取值一律返回 `400 invalid_size`，不会生成任何对象。缩略图按显示方向转正（EXIF），透明背景铺白，去掉元数据，**不放大**。一次解码同时生成三个尺寸。

缩略图与原图同目录，命名为 `{stem}_{size}.jpg`。这条规则不能变更：所有删除逻辑都按它推导出缩略图路径。缩略图只是缓存，不是事实来源：切题图片先校验原图的长度和 SHA-256，再从校验过的字节生成缩略图；缩略图本身不记录摘要。

## 何时生成

1. **写入时登记**：原图入库、补图接受、裁图发布、对话图入库时，在同一事务里把原图路径写入 `image_variant_task`。
2. **后台补齐**：`ImageVariantWorker` 持续处理 `image_variant_task`。缺哪个生成哪个，多实例用 `FOR UPDATE SKIP LOCKED` 加 10 分钟租约分工，对象存储暂时不可用时退避重试，最多 8 次。只有仍持有该次租约的进程才能完成任务或安排重试。原图已删除、摘要不符或无法解码时放弃，并记录 warning。
3. **读取时兜底**：带 `size` 读取时，如果缩略图不存在，就从校验过的原图当场生成并保存，再返回。正确性不依赖后台补齐。

schema v23 升级会把现有任务的原图（含已接受补图）、当前版本裁图，以及所有错题 `source_regions` 里的图片写入 `image_variant_task`，部署后自动补齐历史数据，不需要人工操作。配置 `Emenda__ImageVariants__Enabled=false` 可以停止后台生成（读取兜底仍然有效），`Emenda__ImageVariants__BatchSize` 控制每批数量（默认 8，范围 1–64）。

## 清理

删除原图时一定连带删除它的所有缩略图，且先删原图、后删缩略图：

- 切题裁图与未接受补图：回收队列调用 `DeleteDerivedAsync`，删除对象及三种缩略图的全部 S3 内容版本。
- 删除任务（管理员 `DELETE /admin/api/extractions/{id}`，或获授 `canOperateExtractions` 的 `DELETE /api/ops/extractions/{id}`）：原图经 `DeleteExtractionOriginalAsync` 连同缩略图一起回收；未确认错题的裁图经 `DeleteDerivedAsync` 回收。回收按对象组（对象本身加三种缩略图）复查引用，所以**已确认错题的保留裁图及其缩略图一起保留**，删除后仍可用 `GET .../retained/{itemId}/images/{index}?size=` 或 `GET /api/mistakes/{id}/images/{index}?size=` 读取。
- 任务盘点只登记原图和裁图本身（缩略图命名不满足任务对象路径校验），缩略图从不单独回收，只随父对象整组删除。
- 旧图：`S3OssService.DeleteAsync` 同样先删原图再删缩略图。

并发安全：生成方上传缩略图后会复查原图是否还存在，不存在就删掉刚写入的缩略图。删除方先删原图、再删缩略图。所以要么删除方的清扫看到了缩略图，要么生成方的复查发现原图已经没了，缩略图不会比原图活得更久。后台补齐遇到已删除任务的原图会直接放弃，不会生成。

## 合同

- [mistake-http-v6](mistake-http-v6.json)：新增 `GET /api/mistakes/{id}/images/{index}?size=`（按错题读取，统一覆盖切题、对话和旧错题，任务删除后的保留错题也可读；`no-store`）；签名接口统一尺寸词表，对话图可在通过归属校验后签名缩略图。
- [emenda-extraction-v2](emenda-extraction-v2.json)：原图、裁图和保留裁图读取增加 `size`（`Cache-Control: private, max-age=86400`）。

两份合同都是增量修改（新增路由和可选参数），沿用上游在 #37 中原地演进 v6/v2 的做法。
