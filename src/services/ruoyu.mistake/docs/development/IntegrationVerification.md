# ruoyu.mistake 集成验证方案

> 本文档为 ruoyu.mistake 服务的跨团队联调落地提供验证标准，覆盖 gRPC 接口、跨服务调用、OSS 集成、数据库初始化及配置等关键场景。

---

## 1. 联调测试用例清单

### TC-01 正常创建错题 (student → mistake)

| 项目 | 内容 |
|------|------|
| **用例名称** | 正常创建错题 |
| **目的** | 验证 student 服务通过 gRPC 调用 mistake 的 SubmitMistakeUpload 接口，能成功创建错题条目 |
| **前置条件** | 1. mistake 服务已启动且 gRPC 端口可达<br>2. student 服务已启动<br>3. 数据库连接正常 |
| **输入** | studentId="student-001", subject=3, grade=7, imagePaths=["uploads/2026/test1.jpg"], comments="数学错题", sourceUploadId="upload-001" |
| **操作步骤** | 1. student 服务通过 GrpcMistakeSubmitter 调用 SubmitMistakeUpload<br>2. 传入合法参数<br>3. 检查返回结果 |
| **预期结果** | 1. 返回 success=true<br>2. created_item_ids 非空，包含至少一个有效 GUID<br>3. 数据库 mistake_item 表中新增一条记录，review_status=1(PendingReview)<br>4. process_log 表中新增一条 action=1(Created) 的日志 |
| **失败判断** | 返回 success=false；抛出 gRPC 异常；数据库无新增记录 |

### TC-02 参数非法 — 空 studentId

| 项目 | 内容 |
|------|------|
| **用例名称** | 参数非法 — 空 studentId |
| **目的** | 验证 SubmitMistakeUpload 在 studentId 为空时返回 InvalidArgument |
| **前置条件** | mistake 服务已启动 |
| **输入** | studentId=""（或 null/空白）, subject=3, grade=7, imagePaths=["uploads/test.jpg"], sourceUploadId="upload-001" |
| **操作步骤** | 1. 调用 SubmitMistakeUpload 传入空 studentId<br>2. 检查返回的 gRPC 状态码和消息 |
| **预期结果** | 抛出 RpcException，StatusCode=InvalidArgument，消息包含"缺少学生ID" |
| **失败判断** | 未抛出异常；状态码非 InvalidArgument；错误消息不匹配 |

### TC-03 参数非法 — 无效 subject

| 项目 | 内容 |
|------|------|
| **用例名称** | 参数非法 — 无效 subject |
| **目的** | 验证 SubmitMistakeUpload 在 subject 不在 1-9 范围时返回 InvalidArgument |
| **前置条件** | mistake 服务已启动 |
| **输入** | studentId="student-001", subject=0（或 -1、10、100）, grade=7, imagePaths=["uploads/test.jpg"], sourceUploadId="upload-001" |
| **操作步骤** | 1. 调用 SubmitMistakeUpload 传入无效 subject<br>2. 检查返回的 gRPC 状态码和消息 |
| **预期结果** | 抛出 RpcException，StatusCode=InvalidArgument，消息包含"学科值无效" |
| **失败判断** | 未抛出异常；状态码非 InvalidArgument；错误消息不匹配 |

### TC-04 参数非法 — 无效 grade

| 项目 | 内容 |
|------|------|
| **用例名称** | 参数非法 — 无效 grade |
| **目的** | 验证 SubmitMistakeUpload 在 grade 不在 1-12 范围时返回 InvalidArgument |
| **前置条件** | mistake 服务已启动 |
| **输入** | studentId="student-001", subject=3, grade=0（或 -1、13、99）, imagePaths=["uploads/test.jpg"], sourceUploadId="upload-001" |
| **操作步骤** | 1. 调用 SubmitMistakeUpload 传入无效 grade<br>2. 检查返回的 gRPC 状态码和消息 |
| **预期结果** | 抛出 RpcException，StatusCode=InvalidArgument，消息包含"年级值无效" |
| **失败判断** | 未抛出异常；状态码非 InvalidArgument；错误消息不匹配 |

### TC-05 参数非法 — 空 sourceUploadId

| 项目 | 内容 |
|------|------|
| **用例名称** | 参数非法 — 空 sourceUploadId |
| **目的** | 验证 SubmitMistakeUpload 在 sourceUploadId 为空时返回 InvalidArgument |
| **前置条件** | mistake 服务已启动 |
| **输入** | studentId="student-001", subject=3, grade=7, imagePaths=["uploads/test.jpg"], sourceUploadId="" |
| **操作步骤** | 1. 调用 SubmitMistakeUpload 传入空 sourceUploadId<br>2. 检查返回的 gRPC 状态码和消息 |
| **预期结果** | 抛出 RpcException，StatusCode=InvalidArgument，消息包含"缺少上传记录ID" |
| **失败判断** | 未抛出异常；状态码非 InvalidArgument；错误消息不匹配 |

### TC-06 参数非法 — 空 imagePaths

| 项目 | 内容 |
|------|------|
| **用例名称** | 参数非法 — 空 imagePaths |
| **目的** | 验证 SubmitMistakeUpload 在 imagePaths 为空列表时返回 InvalidArgument |
| **前置条件** | mistake 服务已启动 |
| **输入** | studentId="student-001", subject=3, grade=7, imagePaths=[], sourceUploadId="upload-001" |
| **操作步骤** | 1. 调用 SubmitMistakeUpload 传入空 imagePaths<br>2. 检查返回的 gRPC 状态码和消息 |
| **预期结果** | 抛出 RpcException，StatusCode=InvalidArgument，消息包含"至少需要提供一张图片" |
| **失败判断** | 未抛出异常；状态码非 InvalidArgument；错误消息不匹配 |

### TC-07 分页查询错题列表

| 项目 | 内容 |
|------|------|
| **用例名称** | 分页查询错题列表 |
| **目的** | 验证 GetMistakeItemList 分页参数的默认值和边界行为 |
| **前置条件** | 1. mistake 服务已启动<br>2. 数据库中存在多条错题记录 |
| **输入** | studentId="student-001", subject=0, grade=0, reviewStatus=0, page/size 使用不同组合 |
| **操作步骤** | 1. 传入 page=1, size=10，验证返回分页数据<br>2. 传入 page=0, size=0，验证默认值 page=1, size=10<br>3. 传入 size=200，验证被限制为 max size=100<br>4. 验证 page_meta 中 total_count、total_pages 计算正确 |
| **预期结果** | 1. 正常分页返回数据<br>2. page≤0 时默认为 1，size≤0 时默认为 10<br>3. size>100 时被截断为 100<br>4. total_pages = ceil(total_count / size) |
| **失败判断** | 分页参数未按预期修正；total_pages 计算错误；返回数据量与分页参数不符 |

### TC-08 分页查询待审核上传记录

| 项目 | 内容 |
|------|------|
| **用例名称** | 分页查询待审核上传记录 |
| **目的** | 验证 GetPendingReviewUploads 分页和科目过滤功能 |
| **前置条件** | 1. mistake 服务已启动<br>2. 数据库中存在 PendingReview 状态的错题 |
| **输入** | page=1, size=10, subjects=[3,5]（可选） |
| **操作步骤** | 1. 不传 subjects，验证返回所有待审核上传记录<br>2. 传入 subjects=[3,5]，验证仅返回对应科目的记录<br>3. 传入 page=0, size=0，验证默认值<br>4. 传入 size=200，验证 max size=100 |
| **预期结果** | 1. 无 subjects 过滤时返回全部待审核记录<br>2. 有 subjects 过滤时仅返回匹配科目<br>3. 分页默认值与边界行为同 TC-07<br>4. 返回的 PendingReviewUploadDto 包含 source_upload_id、pending_count、total_count 等字段 |
| **失败判断** | 科目过滤不生效；分页参数未按预期修正；返回字段缺失 |

### TC-09 审核确认单条错题

| 项目 | 内容 |
|------|------|
| **用例名称** | 审核确认单条错题 |
| **目的** | 验证 ReviewMistakeItem 确认操作，状态从 PendingReview 变为 Confirmed |
| **前置条件** | 1. mistake 服务已启动<br>2. 存在一条 PendingReview 状态的错题 |
| **输入** | id=有效错题ID, reviewStatus=Confirmed(2), reviewerId="teacher-001", reviewComment="确认", grade=8(修正), subject=4(修正) |
| **操作步骤** | 1. 调用 ReviewMistakeItem 传入确认参数<br>2. 检查返回 success=true<br>3. 调用 GetMistakeItem 验证状态已更新 |
| **预期结果** | 1. 返回 success=true<br>2. 错题 review_status 变为 Confirmed(2)<br>3. grade 和 subject 被修正为传入值<br>4. reviewer_id、reviewed_at、root_cause 已填充<br>5. process_log 新增 action=3(ReviewConfirmed) 记录 |
| **失败判断** | 返回 success=false；状态未变更；修正字段未生效；无审核日志 |

### TC-10 审核退回单条错题

| 项目 | 内容 |
|------|------|
| **用例名称** | 审核退回单条错题 |
| **目的** | 验证 ReviewMistakeItem 退回操作，以及 teacher_portal 退回后的级联处理 |
| **前置条件** | 1. mistake 服务已启动<br>2. teacher_portal 已启动且 MistakeGrpcService:Address 配置正确<br>3. student 服务已启动<br>4. 存在一条 PendingReview 状态的错题 |
| **输入** | id=有效错题ID, reviewStatus=Rejected(3), reviewerId="teacher-001", reviewComment="图片模糊" |
| **操作步骤** | 1. 通过 teacher_portal POST /api/mistake/review-item 传入退回参数<br>2. 验证 mistake 服务 ReviewMistakeItem 返回 success=true<br>3. 验证 teacher_portal 自动调用 DeleteMistakeItem 删除该错题<br>4. 若该 sourceUploadId 下无其他错题，验证调用 student.MarkUploadRecordAsReturned |
| **预期结果** | 1. ReviewMistakeItem 返回 success=true，状态变为 Rejected(3)<br>2. 错题记录被删除（DeleteMistakeItem）<br>3. 若该上传记录下无剩余错题，student 服务的上传记录被标记为已退回<br>4. process_log 新增 action=4(ReviewRejected) 记录 |
| **失败判断** | 退回后错题未被删除；MarkUploadRecordAsReturned 未被调用（当应调用时）；状态未变更 |

### TC-11 批量审核确认

| 项目 | 内容 |
|------|------|
| **用例名称** | 批量审核确认 |
| **目的** | 验证 teacher_portal BatchReview 接口批量确认多条错题，以及自动触发 CompleteUploadReview |
| **前置条件** | 1. mistake 服务已启动<br>2. teacher_portal 已启动<br>3. student 服务已启动<br>4. 同一 sourceUploadId 下存在多条 PendingReview 错题 |
| **输入** | items=[{id="item-1"},{id="item-2"}], reviewStatus=2(Confirmed), reviewComment="批量确认" |
| **操作步骤** | 1. 调用 POST /api/mistake/batch-review 传入多条确认<br>2. 验证所有错题状态变为 Confirmed<br>3. 验证当同一 sourceUploadId 下无 PendingReview 条目时，自动触发 CompleteUploadReview<br>4. 验证 CompleteUploadReview 成功后调用 student.DeleteUploadRecordAfterReviewAsync |
| **预期结果** | 1. 返回 success=true, processedCount=2, failedCount=0<br>2. 所有错题状态变为 Confirmed<br>3. 自动触发 CompleteUploadReview（OSS 迁移等）<br>4. student 服务删除上传记录 |
| **失败判断** | 部分确认失败；CompleteUploadReview 未自动触发；student 回调未执行 |

### TC-12 批量审核退回

| 项目 | 内容 |
|------|------|
| **用例名称** | 批量审核退回 |
| **目的** | 验证 teacher_portal BatchReview 接口批量退回多条错题，以及级联删除和退回处理 |
| **前置条件** | 1. mistake 服务已启动<br>2. teacher_portal 已启动<br>3. student 服务已启动<br>4. 同一 sourceUploadId 下存在多条 PendingReview 错题 |
| **输入** | items=[{id="item-1"},{id="item-2"}], reviewStatus=3(Rejected), reviewComment="批量退回" |
| **操作步骤** | 1. 调用 POST /api/mistake/batch-review 传入多条退回<br>2. 验证所有错题被 ReviewMistakeItem 退回后自动 DeleteMistakeItem<br>3. 验证当该 sourceUploadId 下无剩余错题时，调用 student.MarkUploadRecordAsReturned |
| **预期结果** | 1. 返回 success=true, processedCount=2, failedCount=0<br>2. 所有错题记录被删除<br>3. student 服务上传记录被标记为已退回 |
| **失败判断** | 部分退回失败；错题未被删除；MarkUploadRecordAsReturned 未被调用（当应调用时） |

### TC-13 完成审核 (CompleteUploadReview)

| 项目 | 内容 |
|------|------|
| **用例名称** | 完成审核 |
| **目的** | 验证 CompleteUploadReview 的完整流程：前提检查、OSS 迁移、路径更新 |
| **前置条件** | 1. mistake 服务已启动<br>2. OSS 服务可用<br>3. sourceUploadId 下所有错题均已审核（无 PendingReview）<br>4. Confirmed 错题的 sourceImagePath 以 "uploads/" 开头 |
| **输入** | sourceUploadId="upload-001", reviewerId="teacher-001" |
| **操作步骤** | 1. 调用 CompleteUploadReview<br>2. 验证前提：无 PendingReview 条目<br>3. 验证 Confirmed 条目：OSS copy uploads/ → mistakes/，删除 uploads/，路径更新<br>4. 验证 Rejected 条目：不迁移<br>5. 验证返回 success=true, studentId 非空 |
| **预期结果** | 1. 返回 success=true<br>2. Confirmed 条目的 sourceImagePath 从 "uploads/xxx" 变为 "mistakes/xxx"<br>3. OSS 中 uploads/ 下原文件已删除，mistakes/ 下存在副本<br>4. Rejected 条目路径不变 |
| **失败判断** | 返回 success=false；OSS 迁移未执行；路径未更新 |

### TC-15 OSS 迁移失败 (单个图片)

| 项目 | 内容 |
|------|------|
| **用例名称** | OSS 迁移失败 — 单个图片 |
| **目的** | 验证 CompleteUploadReview 中单个图片 OSS 迁移失败时不阻塞整体流程 |
| **前置条件** | 1. mistake 服务已启动<br>2. OSS 服务部分可用（某张图片 copy 或 delete 失败） |
| **输入** | sourceUploadId 下有 2 张图片，其中 1 张迁移会失败 |
| **操作步骤** | 1. 模拟某张图片的 OSS CopyObjectAsync 或 DeleteAsync 抛出异常<br>2. 调用 CompleteUploadReview<br>3. 检查返回结果和日志 |
| **预期结果** | 1. 返回 success=true<br>2. 日志中包含 "Error migrating image {Path}"<br>3. 失败图片的路径未更新，成功图片的路径正常更新 |
| **失败判断** | 整体流程被中断；返回 success=false；成功图片的迁移也失败 |

### TC-16 OSS 完全不可用

| 项目 | 内容 |
|------|------|
| **用例名称** | OSS 完全不可用 |
| **目的** | 验证 OSS 服务完全不可用时 CompleteUploadReview 的降级行为 |
| **前置条件** | 1. mistake 服务已启动<br>2. OSS 服务不可达 |
| **输入** | sourceUploadId=有效值（所有条目已审核） |
| **操作步骤** | 1. 停止 OSS 服务或配置错误端点<br>2. 调用 CompleteUploadReview<br>3. 检查返回结果和日志 |
| **预期结果** | 1. 返回 success=true<br>2. 所有图片迁移均失败，日志中每张图片均有 "Error migrating image" 记录<br>3. sourceImagePath 未更新（仍为 uploads/ 前缀） |
| **失败判断** | 返回 success=false；流程被中断；抛出未捕获异常 |

### TC-17 数据库初始化 (PostgreSQL)

在专用 PostgreSQL 数据库中分别验证以下输入：仅有 `public.mistake`、新旧表并存、空旧表、被外键引用的旧表、已有与不存在的迁移历史。调用 `DatabaseInitializer.InitializeAsync` 均应在任何写库步骤前抛出固定的旧 schema 错误；行、索引、外键和历史前后相同，多进程同时启动也全部拒绝。连接检查失败必须中止初始化。其他 schema 中同名表不触发此保护。无 `public.mistake` 时继续既有初始化路径；独立迁移问题可能导致后续失败，不能将其误报为本保护回归。自动化证据位于 `LegacySchemaGuardPostgresTests`，需设置专用的 `MISTAKE_LEGACY_GUARD_TEST_CONNECTION`。

### TC-20 地址错误 (MistakeGrpcService:Address)

| 项目 | 内容 |
|------|------|
| **用例名称** | 地址错误 — MistakeGrpcService:Address |
| **目的** | 验证 teacher_portal 配置的 MistakeGrpcService:Address 错误时，所有 gRPC 调用失败 |
| **前置条件** | teacher_portal 已启动 |
| **输入** | MistakeGrpcService:Address 配置为无效地址 |
| **操作步骤** | 1. 配置 MistakeGrpcService:Address 为错误地址<br>2. 通过 teacher_portal 调用任何 mistake 相关接口<br>3. 检查返回结果 |
| **预期结果** | 1. teacher_portal 返回 HTTP 502 BadGateway<br>2. 日志中包含 RpcException<br>3. 所有 mistake 相关功能不可用 |
| **失败判断** | 返回非 502 状态码；无异常日志 |

### TC-21 GetAllReferencedImagePaths 正常查询

| 项目 | 内容 |
|------|------|
| **用例名称** | GetAllReferencedImagePaths 正常查询 |
| **目的** | 验证获取所有被引用的图片路径，包括错题条目和练习记录中的路径 |
| **前置条件** | 1. mistake 服务已启动<br>2. 数据库中存在错题条目和练习记录 |
| **输入** | Empty 请求 |
| **操作步骤** | 1. 调用 GetAllReferencedImagePaths<br>2. 验证返回的 image_paths 包含所有错题条目 source_regions 中的路径<br>3. 验证包含练习记录 image_paths 中的路径<br>4. 验证路径去重（大小写不敏感）<br>5. 验证空白路径被过滤 |
| **预期结果** | 1. 返回所有非空图片路径的集合<br>2. 路径去重<br>3. 同时包含 mistake_item 和 mistake_practice 中的路径 |
| **失败判断** | 路径缺失；未去重；包含空白路径 |

### TC-22 手动添加错题 (AddMistakeItem)

| 项目 | 内容 |
|------|------|
| **用例名称** | 手动添加错题 |
| **目的** | 验证 teacher 通过 AddMistakeItem 手动创建错题条目 |
| **前置条件** | mistake 服务已启动 |
| **输入** | studentId="student-001", subject=3, grade=7, sourceUploadId="manual-001", sourceRegions=[{sourceImagePath="uploads/manual.jpg", boundingBox={x1=0,y1=0,x2=100,y2=100}}] |
| **操作步骤** | 1. 调用 AddMistakeItem 传入合法参数<br>2. 验证返回 MistakeItemDto<br>3. 验证数据库新增记录 |
| **预期结果** | 1. 返回 MistakeItemDto，id 为有效 GUID<br>2. review_status=PendingReview(1)<br>3. source_regions 正确保存<br>4. process_log 新增 action=1(Created)，details 包含 "manually added" |
| **失败判断** | 返回异常；source_regions 未保存；review_status 不为 PendingReview |

### TC-23 更新错题 (UpdateMistakeItem)

| 项目 | 内容 |
|------|------|
| **用例名称** | 更新错题 |
| **目的** | 验证 UpdateMistakeItem 更新错题字段 |
| **前置条件** | 1. mistake 服务已启动<br>2. 存在一条错题记录 |
| **输入** | id=有效错题ID, subject=5, grade=10, sourceRegions=[更新后的区域] |
| **操作步骤** | 1. 调用 UpdateMistakeItem 传入更新参数<br>2. 验证返回更新后的 MistakeItemDto<br>3. 再次 GetMistakeItem 验证持久化 |
| **预期结果** | 1. 返回更新后的 MistakeItemDto<br>2. subject、grade、source_regions 已更新<br>3. 未传入的字段保持原值 |
| **失败判断** | 返回 NotFound；更新字段未生效；未传入字段被清空 |

### TC-24 删除错题 (DeleteMistakeItem)

| 项目 | 内容 |
|------|------|
| **用例名称** | 删除错题 |
| **目的** | 验证 DeleteMistakeItem 级联删除练习记录和日志 |
| **前置条件** | 1. mistake 服务已启动<br>2. 存在一条错题记录，且有关联的练习记录和日志 |
| **输入** | id=有效错题ID |
| **操作步骤** | 1. 调用 DeleteMistakeItem<br>2. 验证返回 success=true<br>3. 验证错题记录被删除<br>4. 验证关联的 mistake_practice 记录被删除<br>5. 验证关联的 process_log 记录被删除 |
| **预期结果** | 1. 返回 success=true<br>2. mistake_item 记录不存在<br>3. 关联的 mistake_practice 和 process_log 均被级联删除 |
| **失败判断** | 返回 success=false；关联记录未被删除；GetMistakeItem 仍能查到 |

### TC-25 添加练习记录 (AddMistakePractice)

| 项目 | 内容 |
|------|------|
| **用例名称** | 添加练习记录 |
| **目的** | 验证对已确认错题添加练习记录 |
| **前置条件** | 1. mistake 服务已启动<br>2. 存在一条 Confirmed 状态的错题 |
| **输入** | mistakeItemId=有效错题ID, studentId="student-001", studentAnswer="B", isCorrect=false, comments="还需练习", imagePaths=["practice/1.jpg"] |
| **操作步骤** | 1. 调用 AddMistakePractice<br>2. 验证返回 MistakePracticeDto<br>3. 验证数据库新增记录<br>4. 验证 process_log 新增 action=5(Practiced) |
| **预期结果** | 1. 返回 MistakePracticeDto，id 为有效 GUID<br>2. practice_date 已填充<br>3. image_paths 已保存<br>4. process_log 新增 Practiced 记录 |
| **失败判断** | 返回异常；对非 Confirmed 错题添加练习未报错；practice_date 为空 |

> **补充验证：** 对 PendingReview 或 Rejected 状态的错题调用 AddMistakePractice，应返回 InvalidArgument "只能对已确认的错题进行练习"。

### TC-27 处理日志记录与查询

| 项目 | 内容 |
|------|------|
| **用例名称** | 处理日志记录与查询 |
| **目的** | 验证 LogMistakeProcess 写入日志和 GetMistakeProcessHistory 查询日志 |
| **前置条件** | 1. mistake 服务已启动<br>2. 存在一条错题记录 |
| **输入** | mistakeItemId=有效错题ID, studentId="student-001", action=ProcessAction.Mastered(6), details="学生已掌握" |
| **操作步骤** | 1. 调用 LogMistakeProcess 写入自定义日志<br>2. 验证返回 success=true<br>3. 调用 GetMistakeProcessHistory 查询该错题的所有日志<br>4. 验证返回列表包含刚写入的记录 |
| **预期结果** | 1. LogMistakeProcess 返回 success=true<br>2. GetMistakeProcessHistory 返回列表中包含 action=6(Mastered) 的记录<br>3. 日志按 created_at 排序 |
| **失败判断** | 写入返回 false；查询列表中无新增记录；action 值不正确 |

---

## 2. 验证标准

### 通过

- 所有 25 个测试用例的预期结果全部满足
- gRPC 接口在合法输入下返回正确结果
- 参数校验在非法输入下返回 InvalidArgument 且消息匹配
- 跨服务调用（student → mistake、teacher_portal → mistake → student）链路完整
- OSS 迁移在正常情况下路径更新正确
- 数据库初始化在 PostgreSQL 环境下成功
- 容错场景（OSS 单图失败）不影响主流程返回 success=true

### 部分通过

- 核心流程（创建、审核、完成审核）全部通过，但存在以下情况：
  - 非核心接口（如 GetAllReferencedImagePaths）返回结果与预期有偏差但不影响主流程
  - 容错场景日志消息与预期不完全匹配，但功能行为正确
  - 分页边界值（如 total_pages 计算）存在微小偏差
  - teacher_portal 级联操作（退回后删除、确认后 CompleteUploadReview）偶发失败但可重试成功
  - 数据库旧表迁移在特定边界情况下需手动介入

### 阻塞问题

出现以下任一情况视为阻塞，不可上线：

- 正常创建错题（SubmitMistakeUpload）失败
- 审核操作（ReviewMistakeItem）状态未正确变更
- CompleteUploadReview 在所有前提满足时返回 success=false
- 数据库初始化失败导致服务无法启动
- PostgreSQL 连接失败
- OSS 完全不可用时 CompleteUploadReview 抛出未捕获异常（非容错）
- teacher_portal 无法连接 mistake 服务（MistakeGrpcService:Address 配置错误且无法修正）

---

## 3. 异常场景处理机制

### 3.1 OSS 单图迁移失败

| 项目 | 说明 |
|------|------|
| **触发条件** | 单个图片的 CopyObjectAsync 或 DeleteAsync 抛出异常 |
| **影响范围** | 该图片路径未从 uploads/ 更新为 mistakes/，OSS 中可能存在残留 |
| **临时绕过方式** | 无需绕过，其他图片继续迁移 |
| **是否允许降级** | 允许。单图失败不阻塞整体流程 |
| **需要团队介入** | 基础设施团队排查 OSS 异常；mistake 团队事后手动迁移失败图片 |
| **是否影响上线** | 不影响。可通过手动迁移修复 |

### 3.2 OSS 完全不可用

| 项目 | 说明 |
|------|------|
| **触发条件** | OSS 服务不可达或配置错误 |
| **影响范围** | 所有图片迁移失败，sourceImagePath 保持 uploads/ 前缀 |
| **临时绕过方式** | 使用 LocalFileOssService（设置环境变量 USE_LOCAL_OSS=1） |
| **是否允许降级** | 允许降级为本地文件存储 |
| **需要团队介入** | 基础设施团队恢复 OSS 服务 |
| **是否影响上线** | 视情况。若生产环境必须使用 S3 OSS 则阻塞；若可接受本地存储则不阻塞 |

### 3.3 数据库初始化失败 — PostgreSQL 连接失败

| 项目 | 说明 |
|------|------|
| **触发条件** | ConnectionStrings:Default 中的 PostgreSQL 地址/端口/凭据错误，或 PostgreSQL 服务不可达 |
| **影响范围** | 服务无法启动 |
| **临时绕过方式** | 无（服务固定使用 PostgreSQL） |
| **是否允许降级** | 不允许降级 |
| **需要团队介入** | DBA 团队排查 PostgreSQL 连接问题 |
| **是否影响上线** | 阻塞，需修复 PostgreSQL 连接后重新部署 |

### 3.4 数据库初始化失败 — 旧表迁移 SQL 异常

| 项目 | 说明 |
|------|------|
| **触发条件** | 旧 mistake 表结构异常，SQL 执行失败 |
| **影响范围** | 服务无法启动 |
| **临时绕过方式** | 手动备份数据后删除旧 mistake 表，重新启动服务 |
| **是否允许降级** | 不允许自动降级，需手动介入 |
| **需要团队介入** | DBA 团队手动迁移数据 |
| **是否影响上线** | 阻塞，需修复后重新部署 |

### 3.5 MistakeGrpcService:Address 配置错误

| 项目 | 说明 |
|------|------|
| **触发条件** | teacher_portal 配置的 MistakeGrpcService:Address 指向错误地址 |
| **影响范围** | teacher_portal 所有 mistake 相关功能不可用，返回 502 |
| **临时绕过方式** | 修正配置并重启 teacher_portal |
| **是否允许降级** | 不允许降级 |
| **需要团队介入** | 运维团队修正配置 |
| **是否影响上线** | 阻塞 teacher_portal 的错题管理功能 |

### 3.6 ConnectionStrings:Default 缺失

| 项目 | 说明 |
|------|------|
| **触发条件** | appsettings 中未配置 ConnectionStrings:Default 且 Consul 未注入 PostgreSql:* |
| **影响范围** | 服务无法启动 |
| **临时绕过方式** | 配置 PostgreSQL 连接串 |
| **是否允许降级** | 不允许降级 |
| **需要团队介入** | 运维团队配置 Consul 或 appsettings |
| **是否影响上线** | 阻塞，需配置 PostgreSQL 连接串 |

---

## 附录：服务间调用关系

```
student ──(SubmitMistakeUpload)──→ mistake
mistake ──(CompleteUploadReview)──→ OSS

teacher_portal ──(所有 MistakeGrpcService RPC)──→ mistake
teacher_portal ──(MarkUploadRecordAsReturned)──→ student
teacher_portal ──(DeleteUploadRecordAfterReview)──→ student
```

## 附录：ReviewStatus 枚举值

| 名称 | 值 | 说明 |
|------|-----|------|
| REVIEW_STATUS_UNSPECIFIED | 0 | 未指定 |
| REVIEW_STATUS_PENDING_REVIEW | 1 | 待审核 |
| REVIEW_STATUS_CONFIRMED | 2 | 已确认 |
| REVIEW_STATUS_REJECTED | 3 | 已退回 |

## 附录：ProcessAction 枚举值

| 名称 | 值 | 说明 |
|------|-----|------|
| PROCESS_ACTION_UNSPECIFIED | 0 | 未指定 |
| PROCESS_ACTION_CREATED | 1 | 创建 |
| PROCESS_ACTION_ANALYZED | 2 | 分析 |
| PROCESS_ACTION_REVIEW_CONFIRMED | 3 | 审核确认 |
| PROCESS_ACTION_REVIEW_REJECTED | 4 | 审核退回 |
| PROCESS_ACTION_PRACTICED | 5 | 练习 |
| PROCESS_ACTION_MASTERED | 6 | 掌握 |
