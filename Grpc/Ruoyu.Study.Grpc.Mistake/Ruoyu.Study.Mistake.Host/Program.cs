using Ruoyu.Study.Mistake.Infra.Database;
using Ruoyu.Study.Mistake.Service;

var builder = WebApplication.CreateBuilder(args);

// 添加数据库引擎
builder.Services.AddSingleton<MistakeDbEngine>(sp =>
{
    var configuration = sp.GetRequiredService<IConfiguration>();
    return MistakeDbEngine.Create(configuration);
});

// 添加 gRPC 服务
builder.Services.AddGrpc();
builder.Services.AddGrpcHealthChecks();

var app = builder.Build();

// 配置 gRPC 端点
app.MapGrpcService<MistakeAnalysisServiceImpl>();
app.MapGrpcHealthChecksService();

app.MapGet("/", () => "Mistake Analysis gRPC Service is running.");

app.Run();
