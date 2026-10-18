# Mistake 错题本微服务

## 1. 项目概述

`Mistake` 是专为在线教育系统打造的错题管理微服务，专注于处理高频写入和追踪用户学习轨迹。它已从原 `QuestionBank`（该服务其后迁出为独立仓库 [Quaestura](https://github.com/philfanzhou/Quaestura)，ADR-0011）中剥离，负责错题上传、举一反三重练及学习日志管理。

## 2. 📚 文档导航

- [设计文档](docs/overview/Design.md) - 完整架构设计、数据模型、接口定义和关键流程
- [需求文档](docs/overview/Requirements.md) - 详细功能性和非功能性需求
- [数据库设计](docs/database/README.md) - 包含 PostgreSQL 错题记录、重答记录和日志的建表规范
- [编码规范](../../../.agent/rules/coding-policy.md) - 仓库级共享 .NET 项目编码规范

## 3. 技术栈

- **框架**: .NET 10 + ASP.NET Core HTTP
- **数据库**: PostgreSQL (ruoyu_study_mistake)
- **ORM**: Entity Framework Core 10.0 + Npgsql 10
- **对象存储**: MinIO / SeaweedFS / LocalFile (S3 兼容接口)
- **架构**: 清晰分层架构（Contract → Domain → Database → Service → Host）

## 4. 核心功能

### 4.1 错题管理 (Mistake)

| 功能 | 说明 |
|------|------|
| 上传错题 | 学生以图片形式上传原始错题，初始状态为 PendingReview |
| 更新错题 | 修改错题条目信息（学生、学科、年级、图片区域）|
| 删除错题 | 删除任意状态的错题条目，自动级联删除关联的练习记录和处理日志 |
| 查询错题 | 根据学生ID获取错题列表，支持分页和多条件过滤 |

### 4.2 错题重答与举一反三 (Mistake Practice)

| 功能 | 说明 |
|------|------|
| 上传重答 | 记录学生针对特定错题的重答情况和上传的新草稿图片 |
| 获取练习记录 | 查询某道错题的所有历史重答轨迹 |

### 4.3 处理日志 (Process Log)

| 功能 | 说明 |
|------|------|
| 记录日志 | 记录错题被分析、练习、掌握等每一个生命周期节点的行为 |
| 查询日志 | 查询某个错题的处理流转记录 |
