# 提取裁图只读盘点与 #21 迁移边界

本文件记录准备工作，不表示 #21 已实现。当前应用仍保留所有题目的裁图、历史快照、旧纠偏日志和已接受的模型检查点。原图、快照回放及幂等回执仍按现有规则工作。

## 本轮只读观察

2026-11-22 06:33 UTC（北京时间 14:33），核对维护者指定测试机 `192.168.31.5` 的 `emenda` 实例：

- 运行镜像 `ghcr.io/philfanzhou/emenda:0.1.1-rc.7`；源码标签 `a95e202fca7f901599a04626842c5925a333256d`。本轮改动未部署。
- 数据库：1 个上传任务、1 个版本。
- S3 `mistakes/extractions/` 当前对象：60 个，928,604 字节。
- 原图：30 个，615,323 字节；当前版本裁图：30 个，313,281 字节。
- 当前错题条目引用的裁图：3 个，40,007 字节；这 3 个包含在上述 30 个当前版本裁图内，不能重复相加。
- 仅历史版本引用、且没有当前版本或错题条目引用的裁图：0 个。
- 清单之外、需进一步核查的对象：0 个。
- 原图、当前裁图和当前错题引用均未发现对象缺失或长度不符。

该结果为测试环境一次观察；数据库与 S3 依次读取，并非跨系统原子快照。未检查 S3 非当前对象版本、内容 SHA-256、过程日志及检查点的每个引用，不能据此授权删除或声称生产空间已回收。此次没有修改业务数据、删除对象或执行迁移。

## 复现

`script/verification/CropInventory` 复用仓库的 EF/Npgsql 和 Minio 依赖，不初始化或升级数据库。数据库事务强制 `READ ONLY`；S3 仅列举指定前缀。发生数据库或 S3 错误时退出失败，不把空列表冒充成功；输出只含数量和字节数，不含凭据、学生内容或对象地址。

```bash
dotnet build script/verification/CropInventory/CropInventory.csproj
```

标准输入是 JSON 对象，包含以下配置名对应的运行时值：

`ConnectionStrings__Emenda`、`Oss__InternalEndpoint`、`Oss__InternalSecure`、`Oss__AccessKey`、`Oss__SecretKey`、`Oss__BucketName`。

在服务所在网络执行，不传 `--host`。如果从本机使用已核实的测试机发布端口，可传 `--host 192.168.31.5`，仅覆盖盘点进程的数据库和 S3 主机名，保留配置端口；不会修改应用配置。不要把凭据 JSON 保存到项目或粘贴到聊天。示例通过已有 SSH 会话读取配置，直接通过管道传入盘点进程：

```bash
set -o pipefail
ssh -o BatchMode=yes -o ConnectTimeout=8 zhf@192.168.31.5 \
  'python3 -c '\''import json,subprocess; d=json.loads(subprocess.check_output(["docker","inspect","emenda"]))[0]; e=dict(x.split("=",1) for x in d["Config"]["Env"] if "=" in x); keys=["ConnectionStrings__Emenda","Oss__InternalEndpoint","Oss__InternalSecure","Oss__AccessKey","Oss__SecretKey","Oss__BucketName"]; print(json.dumps({k:e[k] for k in keys}))'\''' \
  | dotnet script/verification/CropInventory/bin/Debug/net10.0/CropInventory.dll --host 192.168.31.5
```

对配置值使用受保护的标准输入，不启用 shell tracing，不单独执行会把凭据输出到终端的管道左侧命令。

## #21 后续实现必须同时覆盖

1. 仅 `incorrect` 产生正式裁图；当前错题提供每部分的原图 ID、框和 S3 对象对应关系。顺序或题号变化、未改变的部分可复用有效对象。
2. 新图写好并校验后，在同一个数据库事务中切换当前结果、撤销旧结果并登记持久删除任务。清除旧快照、过程日志、候选检查点、管理端历史恢复和旧回执中的可回放框/图片引用；保留必要操作审计和幂等身份。
3. 数据库提交后尝试删除；失败、响应丢失和进程重启后可继续。仅最后一个有效引用释放后回收，保护原图、补图和共享裁图。
4. 新图写好但发布失败时，失败方独占对象进入可恢复清理；不能误删并发胜出结果。旧键重放不恢复旧裁图。
5. 显式迁移目前不可变历史的数据库约束、合同和测试。存量删除前形成具体清单、回收容量和保护对象验证，再取得对应授权。若 S3 启用了版本控制，必须核对旧对象版本的实际空间回收。

上述改动需要独立的迁移及恢复验证，不能以本轮单题接口上线替代。
