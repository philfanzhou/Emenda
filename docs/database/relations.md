# 实体关系图

## 关系说明

| 关系 | 类型 | 删除行为 | 说明 |
|------|------|----------|------|
| mistake_item → process_log | 1:N | CASCADE | 删除错题时自动删除所有处理日志 |

## JSONB 字段结构

### mistake_item.source_regions

序列化策略：**camelCase**

```json
[
  {
    "sourceImagePath": "uploads/student123/region1.jpg",
    "boundingBox": {
      "x1": 100,
      "y1": 200,
      "x2": 300,
      "y2": 400
    }
  },
  {
    "sourceImagePath": "uploads/student123/region2.jpg",
    "boundingBox": null
  }
]
```

子结构：

| 结构名 | 字段 | 类型 | 说明 |
|--------|------|------|------|
| SourceRegionEntry | `sourceImagePath` | string | 区域图片路径 |
| SourceRegionEntry | `boundingBox` | BoundingBoxEntry? | 边界框（可为 null） |
| BoundingBoxEntry | `x1`, `y1`, `x2`, `y2` | int | 左上角和右下角坐标 |

## 版面检测作业关系

`layout_job`（PK job_id）以 (source_upload_id,content_revision) RESTRICT 引用 `source_intake`，作业永远附着于既有受管来源版本；`layout_job_image` 以 job RESTRICT 挂接每图工作项；`layout_result` 以 job RESTRICT 挂接每代每图唯一接受结果，`layout_result_region` 以 result RESTRICT 挂接区域；`layout_request` 以 job RESTRICT 记录请求键重放。结果不引用 mistake_item——版面结果与条目物化相互独立，旧来源版本的结果留在自己的 (source,revision,result_revision) 行内，不替换新结果。

## 表详细文档

- [mistake_item](./tables/mistake_item.md)
- [mistake_practice](./tables/mistake_practice.md)
- [process_log](./tables/process_log.md)
- layout_job / layout_job_image / layout_result / layout_result_region / layout_request
