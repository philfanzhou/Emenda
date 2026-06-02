using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ruoyu.Study.Common.Oss;
using Ruoyu.Study.Mistake.Database;
using Ruoyu.Study.Mistake.Database.Repositories;
using Ruoyu.Study.Mistake.Domain.Repositories;
using Ruoyu.Study.Mistake.Domain.Services;
using Ruoyu.Study.Mistake.Service;
using StudentProto = Ruoyu.Study.Student.Contract.Protos;

var builder = WebApplication.CreateBuilder(args);

var grpcPort = builder.Configuration.GetValue<int?>("Endpoints:Grpc") ?? 5006;
var httpPort = builder.Configuration.GetValue<int?>("Endpoints:Http") ?? 5007;
var studentGrpcAddress = builder.Configuration["StudentGrpcService:Address"] ?? "http://localhost:5005";

builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(grpcPort, listenOptions =>
    {
        listenOptions.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2;
    });

    options.ListenAnyIP(httpPort, listenOptions =>
    {
        listenOptions.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http1;
    });
});

builder.Services.AddGrpc(options =>
{
    options.MaxReceiveMessageSize = 100 * 1024 * 1024;
    options.MaxSendMessageSize = 100 * 1024 * 1024;
});

// Configure Student service gRPC client
builder.Services.AddGrpcClient<StudentProto.StudentLearningGrpcService.StudentLearningGrpcServiceClient>(options =>
{
    options.Address = new Uri(studentGrpcAddress);
});

var connectionString = builder.Configuration.GetConnectionString("Default");
var isPostgreSql = !string.IsNullOrWhiteSpace(connectionString)
    && (connectionString.Contains("Host=", StringComparison.OrdinalIgnoreCase)
        || connectionString.Contains("Server=", StringComparison.OrdinalIgnoreCase));
builder.Services.AddDbContext<MistakeDbContext>(options =>
{
    if (isPostgreSql)
        options.UseNpgsql(connectionString);
    else
        options.UseSqlite(connectionString ?? "Data Source=data/sqlite/ruoyu_study_mistake.db");
});

builder.Services.Configure<OssOptions>(builder.Configuration.GetSection("Oss"));
builder.Services.AddScoped<IOssService, S3OssService>(sp =>
{
    var options = sp.GetRequiredService<IOptions<OssOptions>>().Value;
    return new S3OssService(options.Endpoint, options.AccessKey, options.SecretKey, options.BucketName);
});

builder.Services.AddScoped<IMistakeItemRepository, MistakeItemRepository>();
builder.Services.AddScoped<IMistakePracticeRepository, MistakePracticeRepository>();
builder.Services.AddScoped<IProcessLogRepository, ProcessLogRepository>();
builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();
builder.Services.AddScoped<MistakeDomainService>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<MistakeDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    await DatabaseInitializer.InitializeAsync(dbContext, logger);
}

app.MapGrpcService<MistakeServiceImpl>();
app.MapGet("/", () => "Mistake gRPC host is running.");

app.Run();
