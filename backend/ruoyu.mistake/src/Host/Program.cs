using Microsoft.EntityFrameworkCore;
using Ruoyu.Study.Mistake.Database;
using Ruoyu.Study.Mistake.Database.Repositories;
using Ruoyu.Study.Mistake.Domain.Repositories;
using Ruoyu.Study.Mistake.Components.Oss;
using Ruoyu.Study.Mistake.Domain.Services;
using Ruoyu.Study.Mistake.Service;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddGrpc();
builder.Services.AddDbContext<MistakeDbContext>(options =>
{
    var connectionString = builder.Configuration.GetConnectionString("Default");
    if (string.IsNullOrEmpty(connectionString))
    {
        throw new ArgumentException("PostgreSQL连接字符串不能为空");
    }
    options.UseNpgsql(connectionString);
});

// Register S3 OSS service
var ossOptions = builder.Configuration.GetSection("Oss").Get<OssOptions>() ?? new OssOptions();
builder.Services.AddSingleton<IOssService>(new S3OssService(
    ossOptions.Endpoint,
    ossOptions.AccessKey,
    ossOptions.SecretKey,
    ossOptions.BucketName));

builder.Services.AddScoped<IMistakeRepository, MistakeRepository>();
builder.Services.AddScoped<IMistakeDetailRepository, MistakeDetailRepository>();
builder.Services.AddScoped<IMistakePracticeRepository, MistakePracticeRepository>();
builder.Services.AddScoped<IProcessLogRepository, ProcessLogRepository>();
builder.Services.AddScoped<IImageFingerprintRepository, ImageFingerprintRepository>();
builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();
builder.Services.AddScoped<MistakeDomainService>();

var app = builder.Build();

// Configure database initialization (auto-migrate on startup)
using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<MistakeDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    await DatabaseInitializer.InitializeAsync(dbContext, logger);
}

// Configure the HTTP request pipeline.
app.MapGrpcService<MistakeServiceImpl>();
app.MapGet("/", () => "Communication with gRPC endpoints must be made through a gRPC client. To learn how to create a client, visit: https://go.microsoft.com/fwlink/?linkid=2086909");

app.Run();
