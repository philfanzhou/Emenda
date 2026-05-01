# .NET 解决方案编码规范

## 1. 总则

本文档规定了 .NET 解决方案的编码标准和技术约束，适用于任何需要遵循此规范的 .NET 项目。所有参与该解决方案开发的开发人员必须严格遵守。

### 1.1 适用范围
- 解决方案内的所有 C# 项目（`.csproj`）
- 所有源代码文件（`.cs`）
- 所有测试项目
- 所有配置文件

### 1.2 核心原则
- **稳定性优先**：选择经过生产环境验证的技术栈
- **最小依赖**：在满足功能需求的前提下，使用最低可行的 .NET 版本
- **兼容性保障**：确保组件具有良好的跨版本兼容性
- **可维护性**：代码风格统一，易于理解和维护
- **避免过期技术**：不使用已停止支持或过期的 .NET 版本

---

## 2. .NET 版本约束

### 2.1 最高版本限制
**本解决方案支持的最高 .NET 版本为 .NET 8.0。**

解决方案内的所有项目**必须** targeting .NET 8.0 或更低版本。

**理由：**
- **稳定性**：.NET 8.0 是长期支持（LTS）版本，在生产环境中具有 proven 的稳定性
- **兼容性**：确保与现有基础设施和部署流水线的兼容性
- **维护成本**：减少强制升级和破坏性变更的频率
- **支持周期**：避免使用已过期或即将停止支持的版本

### 2.2 允许使用的 .NET 版本

#### 优先级顺序（从高到低）：

1. **.NET Standard 2.0/2.1**（最佳）
   - 适用于不依赖 ASP.NET Core 特定功能的类库
   - 提供最大的跨平台和跨版本兼容性
   - 可被 .NET Framework、.NET Core、.NET 5+ 项目引用
   - **首选用于纯类库项目**

2. **.NET 8.0**（可接受）
   - Web API 项目、ASP.NET Core 应用
   - 需要 .NET 8.0 特有功能的场景
   - **禁止降级到已过期版本**

#### 禁止使用的版本：
- ❌ **.NET 6.0** - 已过期（Out of Support），不再接收安全更新
- ❌ **.NET 5.0 及更早的 .NET Core 版本** - 已过期
- ❌ **.NET Framework 4.x** - 除非有明确的遗留系统兼容性要求

### 2.3 决策标准

**使用 .NET Standard 的场景：**
- ✅ 项目是纯类库（Class Library）
- ✅ 不依赖 ASP.NET Core 托管特定的 API
- ✅ 需要最大化的跨平台和跨版本兼容性
- ✅ 库需要被 .NET Framework、.NET Core 或 .NET 5+ 项目引用

**使用 .NET 8.0 的场景：**
- ✅ Web API 项目（ASP.NET Core）
- ✅ 需要 ASP.NET Core 特定功能
- ✅ 需要使用 .NET 8.0 独有的 API 或性能特性
- ✅ 控制台应用、Windows 服务等可执行项目

**禁止使用 .NET 6.0 的理由：**
- ❌ .NET 6.0 已于 2024 年 11 月结束支持
- ❌ 不再接收安全更新和补丁
- ❌ 存在潜在的安全风险
- ❌ 不符合企业级应用的安全合规要求

### 2.4 项目文件配置示例

**类库（优先使用 .NET Standard）：**
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>netstandard2.0</TargetFramework>
    <LangVersion>latest</LangVersion>
  </PropertyGroup>
</Project>
```

**Web API 项目：**
```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <LangVersion>latest</LangVersion>
  </PropertyGroup>
</Project>
```

**测试项目（与被测项目的框架版本匹配）：**
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <LangVersion>latest</LangVersion>
  </PropertyGroup>
</Project>
```

### 2.5 多目标框架（仅在必要时使用）

如果库必须支持多个框架：
```xml
<PropertyGroup>
  <TargetFrameworks>netstandard2.0;net8.0</TargetFrameworks>
</PropertyGroup>
```

**注意**：多目标框架会增加复杂性，仅在以下情况使用：
- 库被广泛消费于不同的 .NET 版本
- 性能优势证明了增加的复杂性是合理的
- 条件编译是可控的

---

## 3. NuGet 包依赖管理

### 3.1 版本选择规则
- **使用最新稳定版本**：选择与目标框架兼容的最新稳定版 NuGet 包
- **避免预览版本**：除非关键功能必需，否则不使用预览版
- **确保积极维护**：优先选择 12 个月内有更新的包
- **检查包兼容性**：确保包与目标 .NET 版本兼容

### 3.2 避免重复依赖（重要）

**原则：如果引用的项目已经依赖了相同的包，就不要重复显式声明依赖。**

#### 正确做法：
```xml
<!-- 假设 ProjectA 已经引用了 Newtonsoft.Json -->
<Project Sdk="Microsoft.NET.Sdk">
  <!-- ✅ 正确：不需要再次引用 Newtonsoft.Json -->
  <ItemGroup>
    <ProjectReference Include="..\ProjectA\ProjectA.csproj" />
  </ItemGroup>
</Project>
```

#### 错误做法：
```xml
<!-- 假设 ProjectA 已经引用了 Newtonsoft.Json -->
<Project Sdk="Microsoft.NET.Sdk">
  <!-- ❌ 错误：重复引用了 ProjectA 已经依赖的包 -->
  <ItemGroup>
    <ProjectReference Include="..\ProjectA\ProjectA.csproj" />
    <PackageReference Include="Newtonsoft.Json" Version="13.0.4" />
  </ItemGroup>
</Project>
```

#### 检查清单：
在添加包引用前，**必须**确认：
- [ ] 检查所有引用的项目（`<ProjectReference>`）是否已经传递依赖了该包
- [ ] 使用 `dotnet list package` 命令查看实际引用的包
- [ ] 如果传递依赖的版本满足需求，就不要显式引用
- [ ] 只有在以下情况才显式引用：
  - 需要特定版本（覆盖传递依赖的版本）
  - 没有项目传递依赖该包
  - 需要直接控制版本升级

### 3.3 版本对齐
- 解决方案中的所有项目**应该**对共享包使用**相同的主版本**
- 包版本**必须**与解决方案中的最低目标框架兼容
- 当包需要更高的 .NET 版本时，评估：
  - 是否存在框架要求更低的替代包
  - 是否可以在不使用该包的情况下实现功能
  - 升级整个解决方案是否合理

### 3.4 包引用格式

**正确做法：**
```xml
<PackageReference Include="PackageName" Version="8.0.4" />
```

**禁止使用：**
- ❌ 浮动版本（如 `Version="8.0.*"`）
- ❌ 版本范围（如 `Version="[8.0,9.0)"`）
- ❌ 重复引用（项目已传递依赖的包）

### 3.5 常用包版本规范

| 包类别 | 版本策略 | 说明 |
|--------|----------|------|
| Microsoft.AspNetCore.* | 与 TFM 主版本一致 | 如 net8.0 使用 8.x.x 版本 |
| EntityFrameworkCore | 最新稳定版 | 确保与 TFM 兼容 |
| 第三方库 | 最新稳定版 | 确保积极维护 |
| 测试框架 | 最新稳定版 | NUnit/xUnit/Moq 等 |

---

## 4. C# 语言规范

### 4.1 语言版本
- 所有项目必须使用**最新**的语言版本
- 在 `.csproj` 文件中设置：`<LangVersion>latest</LangVersion>`

### 4.2 命名规范

#### 类命名
- 类名**必须**简单且简短，避免冗长描述性前缀/后缀
- 去掉不必要的上下文重复词（如项目名、模块名等已由命名空间表达的信息）
- ✅ 正确：`RegistryEntry`、`ConfigSnapshot`、`HeartbeatService`
- ❌ 错误：`EngineRegistryEntry`、`InMemoryConfigSnapshot`、`EngineHeartbeatService`
- 文件名与类名匹配，同样保持简短

#### 方法命名
- 方法名**优先**使用 `private` 可见性
- 仅在确实需要跨类访问时才提升为 `internal`
- **禁止**使用 `public`，除非类型实现了必须公开的接口或被外部程序集引用
- 可见性优先级：`private` > `internal` > `public`

#### 命名原则
- **最小暴露**：不必要 public 就不要 public，function 优先 private，实在需要才 internal
- **职责单一**：每个类只做一件事，如果类承担了多个职责应拆分
- **简洁优先**：名称应传达意图但不冗余，上下文由命名空间提供

```csharp
// ✅ Correct: Simple, short, minimal visibility
namespace MPS.Agent.Engine.Services;

internal class HeartbeatService : BackgroundService
{
    private static int DetectPort() { ... }
    private async Task RegisterIfNeededAsync() { ... }
}

// ❌ Incorrect: Redundant prefix, unnecessary public
namespace MPS.Agent.Engine.Services;

public class EngineHeartbeatService : BackgroundService
{
    public static int DetectListeningPort() { ... }
    public async Task RegisterIfNeededAsync() { ... }
}
```

### 4.3 代码风格

#### 命名空间声明
- **必须**使用文件作用域命名空间（分号结尾），**禁止**使用块式大括号
- ✅ 正确：`namespace MyNamespace;`
- ❌ 错误：`namespace MyNamespace { ... }`

#### Using 语句
- 移除所有**未使用**的 using 语句
- 仅保留必要的引用
- 在适当的情况下使用隐式 usings

#### 文件组织
- 一个文件一个类（通常情况）
- 文件名应与类名匹配
- 使用一致的命名约定

### 4.4 注释和字符串
- 所有注释**必须**使用**英文**
- 所有输出/显示的字符串字面量**必须**使用**英文**
- 代码注释和输出消息中**禁止**出现中文字符

**示例：**
```csharp
// ✅ Correct: Initialize the agent service
var agent = new AgentService();

// ❌ Incorrect: 初始化代理服务
var agent = new AgentService();
```

### 4.5 可空引用类型
- 所有新项目**必须**启用可空引用类型
- 在 `.csproj` 中设置：`<Nullable>enable</Nullable>`
- 正确处理可空警告，避免不必要的 `!` 操作符

### 4.6 异步编程
- 异步方法**必须**使用 `Async` 后缀命名
- 避免使用 `async void`，除非是事件处理程序
- 优先使用 `Task` 而不是 `ValueTask`（除非性能关键）
- 使用 `ConfigureAwait(false)` 在库代码中避免上下文捕获

```csharp
// ✅ Correct
public async Task<User> GetUserAsync(int id)
{
    return await _repository.GetByIdAsync(id).ConfigureAwait(false);
}

// ❌ Incorrect
public async void GetUser(int id) { ... }
```

---

## 5. 项目结构规范

### 5.1 解决方案组织
```
Solution.sln
├── src/
│   ├── WebApi/                    # 主项目（Web API）
│   ├── Core/                      # 核心类库
│   ├── Services/                  # 业务服务类库
│   └── ...
├── test/
│   └── Solution.Tests/            # 测试项目
├── docs/                          # 文档
└── ...
```

### 5.2 项目引用规则
- 项目引用**必须**使用 `<ProjectReference>` 而非包引用
- 避免循环依赖
- 依赖关系应该单向流动（Controller → Service → Repository）
- **检查传递依赖**：避免重复引用项目已依赖的 NuGet 包

### 5.3 条件引用
对于仅在特定配置下需要的引用，使用条件引用：
```xml
<ItemGroup Condition="'$(Configuration)' == 'Debug'">
  <ProjectReference Include="..\Mocks\Mocks.csproj" />
</ItemGroup>
```

---

## 6. 测试规范

### 6.1 测试框架
- 使用 **NUnit** 或 **xUnit** 作为测试框架
- 使用 **Moq** 或 **NSubstitute** 作为 Mock 框架
- 使用断言库（如 NUnit/ xUnit 内置断言或第三方断言库）增强断言可读性

### 6.2 测试项目配置
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <LangVersion>latest</LangVersion>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
  </PropertyGroup>
</Project>
```

### 6.3 测试代码规范
- 测试类名与被测类名对应，后缀 `Tests`
- 测试方法使用 `[Test]` 或 `[Fact]` 特性
- 测试方法命名：`MethodName_Scenario_ExpectedResult`
- 每个测试只验证一个行为

```csharp
[Test]
public async Task GetUserAsync_InvalidId_ThrowsNotFoundException()
{
    // Arrange
    var invalidId = -1;
    
    // Act & Assert
    Assert.ThrowsAsync<NotFoundException>(() => _service.GetUserAsync(invalidId));
}
```

### 6.4 测试覆盖率
- 关键业务逻辑**必须**有单元测试覆盖
- 目标覆盖率：核心模块 ≥ 80%
- 使用 coverlet 收集覆盖率数据

---

## 7. 构建和编译要求

### 7.1 编译要求
- 所有解决方案**必须**无错误编译
- 所有单元测试**必须**通过
- **禁止**出现未使用引用的警告

### 7.2 隐式 Usings
- Web 项目和新类库**可以**启用隐式 usings
- 在 `.csproj` 中设置：`<ImplicitUsings>enable</ImplicitUsings>`
- 传统类库可根据情况选择是否启用

### 7.3 全局配置（可选）
在解决方案根目录创建 `Directory.Build.props` 统一配置：
```xml
<Project>
  <PropertyGroup>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
</Project>
```

---

## 8. 合规性检查清单

在合并任何 PR 之前，**必须**验证：

### 8.1 .NET 版本合规性
- [ ] 所有项目 targeting .NET 8.0 或 .NET Standard 2.0/2.1
- [ ] **没有使用 .NET 6.0 或其他已过期版本**
- [ ] 目标框架是最低可行版本
- [ ] 类库在可能时使用 .NET Standard

### 8.2 包依赖合规性
- [ ] 所有 NuGet 包与目标框架兼容
- [ ] 包版本是明确的（无浮动版本）
- [ ] **没有重复引用项目已依赖的包**
- [ ] 包是积极维护的（12 个月内有更新）
- [ ] 使用 `dotnet list package` 验证实际依赖

### 8.3 代码规范合规性
- [ ] 使用文件作用域命名空间
- [ ] 注释和字符串使用英文
- [ ] 无未使用的 using 语句
- [ ] 启用了可空引用类型
- [ ] 异步方法命名正确
- [ ] 类名简短无冗余前缀
- [ ] 文件名与类名匹配且简短
- [ ] 可见性最小化（private 优先，internal 次之，public 仅必要时）
- [ ] 每个类职责单一

### 8.4 测试合规性
- [ ] 所有单元测试通过
- [ ] 关键逻辑有测试覆盖
- [ ] 测试项目名称规范

### 8.5 编译合规性
- [ ] 解决方案无错误编译
- [ ] 无警告（特别是未使用引用警告）

---

## 9. 例外流程

任何偏离本规范的情况**必须**遵循以下流程：

1. **书面说明**：文档化为什么不能使用标准做法
2. **影响分析**：评估对其他项目和部署的影响
3. **审批**：必须经过架构团队审查和批准
4. **记录在案**：在代码中添加注释说明例外原因

---

## 10. 迁移路径

对于需要合规的现有项目：

1. **审计依赖**：列出所有依赖及其框架要求
2. **识别过期版本**：特别检查是否使用了 .NET 6.0 或其他已过期版本
3. **检查重复依赖**：使用 `dotnet list package` 识别重复引用的包
4. **渐进式更新**：逐步更新项目文件
5. **完整测试**：每次更改后运行完整测试套件
6. **更新文档**：同步更新相关文档

---

## 11. 违规处理

- **轻微违规**：在 Code Review 中指出并修正
- **严重违规**（如使用过期 .NET 版本）：PR 不予合并，必须修正后重新提交
- **重复违规**：上报至技术负责人

---

## 附录 A：常用配置模板

### A.1 Web API 项目
```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <LangVersion>latest</LangVersion>
  </PropertyGroup>
</Project>
```

### A.2 类库（推荐 .NET Standard）
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>netstandard2.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <LangVersion>latest</LangVersion>
  </PropertyGroup>
</Project>
```

### A.3 测试项目
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
    <LangVersion>latest</LangVersion>
  </PropertyGroup>
</Project>
```

---

## 附录 B：检查重复依赖的命令

使用以下 .NET CLI 命令检查项目依赖：

```bash
# 查看项目引用的所有包（包括传递依赖）
dotnet list package

# 查看特定项目的包依赖
dotnet list <project.csproj> package

# 查看包依赖树
dotnet list package --include-transitive

# 检查包过时情况
dotnet list package --outdated
```

---

**文档版本**: 1.0  
**生效日期**: 2026-04-22  
**最后更新**: 2026-04-22  
**所有者**: 架构团队  
**适用范围**: 任何 .NET 解决方案
