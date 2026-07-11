using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ruoyu.Study.Common.Ai;
using Ruoyu.Study.Common.Oss;
using Ruoyu.Study.Mistake.Database;
using Ruoyu.Study.Mistake.Database.Repositories;
using Ruoyu.Study.Mistake.Domain.Repositories;
using Ruoyu.Study.Mistake.Domain.Services;
using Ruoyu.Study.Mistake.Host.Services;
using Ruoyu.Study.Mistake.Host.Services.VlImageAnalysis;
using Ruoyu.Study.Mistake.Service;
using Ruoyu.Study.Consul.Shared;
using StudentProto = Ruoyu.Study.Student.Contract.Protos;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddRuoyuConsulConfiguration(builder.Configuration);

var consulOptions = RuoyuConsulOptions.Bind(builder.Configuration);
var consulRuntimeState = RuoyuConsulRuntimeState.Instance;

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

var connectionString = SharedPostgreSqlConnectionStringFactory.BuildOrFallback(
    builder.Configuration,
    builder.Configuration.GetConnectionString("Default"));
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

builder.Services.AddScoped<IMistakeItemRepository, MistakeItemRepository>();
builder.Services.AddScoped<IMistakePracticeRepository, MistakePracticeRepository>();
builder.Services.AddScoped<IProcessLogRepository, ProcessLogRepository>();
builder.Services.AddScoped<IReanalyzeJobRepository, ReanalyzeJobRepository>();
builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();
builder.Services.AddScoped<IMistakeDomainService, MistakeDomainService>();
builder.Services.AddScoped<IReanalyzeJobService, ReanalyzeJobService>();

// OSS service (Mistake direct access, allowed prefixes: mistakes/, uploads/)
// uploads/ prefix needed for polling worker to read and migrate images
var ossConfig = new OssOptions();
builder.Configuration.GetSection("Oss").Bind(ossConfig);
builder.Services.AddSingleton<IOssService>(sp =>
{
    return new S3OssService(ossConfig.Endpoint, ossConfig.AccessKey, ossConfig.SecretKey, ossConfig.BucketName,
        new[] { "mistakes/", "uploads/" }, ossConfig.PublicEndpoint);
});

// VL Analyzer for image grouping
builder.Services.Configure<VlAnalyzerOptions>(builder.Configuration.GetSection("VlAnalyzer"));
// Register OpenAiCompatibleClient in non-streaming mode (HttpClient.Timeout finite,
// per-attempt timeout enforced by the client). OpenAiCompatibleClient's constructor
// configures BaseAddress + Bearer auth on the HttpClient, so AddHttpClient only needs
// to register the named client for pooling.
builder.Services.AddHttpClient(nameof(OpenAiCompatibleClient));
builder.Services.AddSingleton<OpenAiCompatibleClient>(sp =>
{
    var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
    var httpClient = httpClientFactory.CreateClient(nameof(OpenAiCompatibleClient));
    var options = sp.GetRequiredService<IOptions<VlAnalyzerOptions>>().Value;
    var logger = sp.GetRequiredService<ILogger<OpenAiCompatibleClient>>();
    return new OpenAiCompatibleClient(httpClient, options, logger);
});
builder.Services.AddSingleton<Ruoyu.Study.Mistake.Service.Services.IVlAnalyzer, VlUploadAnalyzer>();

// Polling worker: polls Student service for Processing records and processes them
builder.Services.AddHostedService<UploadPollingWorker>();

// Reanalyze job workers: process AI re-analysis jobs asynchronously.
// MaxConcurrency is hardcoded to 2 inside the service (do not externalize).
builder.Services.AddHostedService<ReanalyzeJobHostedService>();
builder.Services.AddHostedService<ReanalyzeJobCleanupService>();

// Student Learning gRPC client (used by polling worker and review flow)
builder.Services.AddSingleton(sp =>
{
    var grpcUrl = builder.Configuration["StudentLearningService:GrpcUrl"]
                  ?? builder.Configuration["StudentService:GrpcUrl"]
                  ?? "http://localhost:5005";
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

    var channel = Grpc.Net.Client.GrpcChannel.ForAddress(grpcUrl, new Grpc.Net.Client.GrpcChannelOptions
    {
        HttpHandler = handler,
        MaxRetryAttempts = 3,
    });

    return new StudentProto.StudentLearningGrpcService.StudentLearningGrpcServiceClient(channel);
});

var app = builder.Build();

app.Logger.LogInformation("Mistake Service starting");
app.Logger.LogInformation("Endpoints: gRPC={GrpcPort}, HTTP={HttpPort}", grpcPort, httpPort);
app.Logger.LogInformation(
    "Consul startup diagnostics: Address={Address}, Token={Token}, Source={Source}, KeyCount={KeyCount}, Prefixes={Prefixes}, LastError={LastError}",
    $"{consulOptions.Host}:{consulOptions.Port}",
    StartupDiagnosticsFormatter.MaskSecret(consulOptions.Token),
    consulRuntimeState.Source,
    consulRuntimeState.KeyCount,
    StartupDiagnosticsFormatter.SummarizePrefixes(consulRuntimeState.LoadedPrefixes),
    StartupDiagnosticsFormatter.SummarizeError(consulRuntimeState.LastError));
if (isPostgreSql && !string.IsNullOrEmpty(connectionString))
{
    var csb = new DbConnectionStringBuilder { ConnectionString = connectionString };
    app.Logger.LogInformation("Database: PostgreSQL {Host}:{Port}/{Database}", csb["Host"], csb.TryGetValue("Port", out var dbPort) ? dbPort : "5432", csb["Database"]);
}
else
{
    app.Logger.LogInformation("Database: SQLite");
}
app.Logger.LogInformation(
    "Effective configuration diagnostics: PostgreSqlHost={PostgreSqlHost}, PostgreSqlPort={PostgreSqlPort}, PostgreSqlUsername={PostgreSqlUsername}, PostgreSqlPassword={PostgreSqlPassword}, DatabaseName={DatabaseName}, StudentGrpcUrl={StudentGrpcUrl}",
    StartupDiagnosticsFormatter.SummarizeValue(builder.Configuration["PostgreSql:Host"]),
    StartupDiagnosticsFormatter.SummarizeValue(builder.Configuration["PostgreSql:Port"]),
    StartupDiagnosticsFormatter.SummarizeValue(builder.Configuration["PostgreSql:Username"]),
    StartupDiagnosticsFormatter.SummarizePassword(builder.Configuration["PostgreSql:Password"]),
    StartupDiagnosticsFormatter.SummarizeValue(builder.Configuration["Database:Name"]),
    StartupDiagnosticsFormatter.SummarizeValue(builder.Configuration["StudentLearningService:GrpcUrl"]));

var vlConfig = builder.Configuration.GetSection("VlAnalyzer");
var vlOptions = vlConfig.Get<VlAnalyzerOptions>() ?? new VlAnalyzerOptions();
app.Logger.LogInformation("VL Analyzer: Configured={IsConfigured}, Model={Model}, BaseUrl={BaseUrl}, ApiKey={ApiKeyStatus}",
    vlOptions.IsConfigured, vlOptions.Model, vlOptions.BaseUrl,
    string.IsNullOrEmpty(vlOptions.ApiKey) ? "(empty - service disabled)" : SensitiveDataMasker.MaskApiKey(vlOptions.ApiKey));

var studentGrpcUrl = builder.Configuration["StudentLearningService:GrpcUrl"]
                     ?? builder.Configuration["StudentService:GrpcUrl"]
                     ?? "http://localhost:5005";
app.Logger.LogInformation("Student Service: GrpcUrl={Url}", studentGrpcUrl);

app.Logger.LogInformation("OSS: Endpoint={Endpoint}, Bucket={Bucket}, Prefixes=mistakes/,uploads/",
    ossConfig.Endpoint ?? "N/A", ossConfig.BucketName ?? "N/A");
app.Logger.LogInformation("UploadPollingWorker: Enabled=true, PollInterval=60s");
app.Logger.LogInformation("ReanalyzeJobHostedService: Enabled=true, MaxConcurrency=2 (hardcoded)");
app.Logger.LogInformation("ReanalyzeJobCleanupService: Enabled=true, RetentionDays=30 (hardcoded)");

using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<MistakeDbContext>();
    var loggerFactory = scope.ServiceProvider.GetRequiredService<ILoggerFactory>();
    await DatabaseInitializer.InitializeAsync(dbContext, loggerFactory);
}

app.MapGrpcService<MistakeServiceImpl>();
app.MapGet("/", () => "Mistake gRPC host is running.");

app.Run();
