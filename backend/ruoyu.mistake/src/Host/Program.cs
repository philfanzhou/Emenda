using Grpc.Net.Client;
using Microsoft.EntityFrameworkCore;
using Ruoyu.Study.Common.Oss;
using Ruoyu.Study.Mistake.Database;
using Ruoyu.Study.Mistake.Database.Repositories;
using Ruoyu.Study.Mistake.Domain.Repositories;
using Ruoyu.Study.Mistake.Domain.Services;
using Ruoyu.Study.Mistake.Service;
using Ruoyu.Study.Student.Contract.Protos;

var builder = WebApplication.CreateBuilder(args);

var grpcPort = builder.Configuration.GetValue<int?>("Endpoints:Grpc") ?? 5006;
var httpPort = builder.Configuration.GetValue<int?>("Endpoints:Http") ?? 5007;

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
builder.Services.AddDbContext<MistakeDbContext>(options =>
{
    var connectionString = builder.Configuration.GetConnectionString("Default");
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        throw new ArgumentException("PostgreSQL connection string is required.");
    }

    options.UseNpgsql(connectionString);
});

var ossOptions = builder.Configuration.GetSection("Oss").Get<OssOptions>() ?? new OssOptions();
builder.Services.AddSingleton<IOssService>(new S3OssService(
    ossOptions.Endpoint,
    ossOptions.AccessKey,
    ossOptions.SecretKey,
    ossOptions.BucketName));

builder.Services.AddSingleton(sp =>
{
    var grpcUrl = builder.Configuration["StudentService:GrpcUrl"] ?? "http://localhost:5005";
    var logger = sp.GetRequiredService<ILogger<Program>>();
    logger.LogInformation("Connecting to Student Learning gRPC: {Url}", grpcUrl);

    var handler = new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(5),
        MaxConnectionsPerServer = 10,
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        EnableMultipleHttp2Connections = true,
    };

    var channel = GrpcChannel.ForAddress(grpcUrl, new GrpcChannelOptions
    {
        HttpHandler = handler,
        MaxRetryAttempts = 3,
    });

    return new StudentLearningGrpcService.StudentLearningGrpcServiceClient(channel);
});

builder.Services.AddScoped<IMistakeRepository, MistakeRepository>();
builder.Services.AddScoped<IMistakeDetailRepository, MistakeDetailRepository>();
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
