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
using Ruoyu.Study.MistakeBff.HttpClients;
using Ruoyu.Study.Mistake.Host;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddRuoyuConsulConfiguration(builder.Configuration);

// ========== Serilog (Console + Grafana Loki) ==========
// Loki 地址统一来自配置键 Loki:Uri（优先由 Consul KV 提供），覆盖 appsettings.json 中的 fallback。
// Loki Sink 在 uri 为 null 时会抛 ArgumentNullException，配置文件中提供 fallback uri 确保服务能启动。
// Loki 不可达时 Sink 异步重试，不影响服务运行。
var lokiUri = builder.Configuration["Loki:Uri"];
if (!string.IsNullOrWhiteSpace(lokiUri))
{
    builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Serilog:WriteTo:1:Args:uri"] = lokiUri
    });
}

builder.Host.UseAgentSerilog("Ruoyu.Study.Mistake");

var consulOptions = RuoyuConsulOptions.Bind(builder.Configuration);
var consulRuntimeState = RuoyuConsulRuntimeState.Instance;

var httpPort = builder.Configuration.GetValue<int?>("Endpoints:Http") ?? 5007;

builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(httpPort, listenOptions =>
    {
        listenOptions.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http1;
    });
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

// Student HTTP client (used by polling worker and review flow).
// Prefer the new HTTP key, but keep legacy fallbacks so old local/test configs do not break.
var studentBaseUrl = builder.Configuration["StudentService:Url"]
                     ?? builder.Configuration["StudentService:BaseUrl"]
                     ?? builder.Configuration["StudentService:GrpcUrl"]
                     ?? "http://localhost:5005";
builder.Services.AddStudentHttpClient(studentBaseUrl);

var app = builder.Build();

app.Logger.LogInformation("Mistake Service starting");
app.Logger.LogInformation("Endpoint: HTTP={HttpPort}", httpPort);
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
    "Effective configuration diagnostics: PostgreSqlHost={PostgreSqlHost}, PostgreSqlPort={PostgreSqlPort}, PostgreSqlUsername={PostgreSqlUsername}, PostgreSqlPassword={PostgreSqlPassword}, DatabaseName={DatabaseName}, StudentBaseUrl={StudentBaseUrl}",
    StartupDiagnosticsFormatter.SummarizeValue(builder.Configuration["PostgreSql:Host"]),
    StartupDiagnosticsFormatter.SummarizeValue(builder.Configuration["PostgreSql:Port"]),
    StartupDiagnosticsFormatter.SummarizeValue(builder.Configuration["PostgreSql:Username"]),
    StartupDiagnosticsFormatter.SummarizePassword(builder.Configuration["PostgreSql:Password"]),
    StartupDiagnosticsFormatter.SummarizeValue(builder.Configuration["Database:Name"]),
    StartupDiagnosticsFormatter.SummarizeValue(
        builder.Configuration["StudentService:Url"]
        ?? builder.Configuration["StudentService:BaseUrl"]
        ?? builder.Configuration["StudentService:GrpcUrl"]));

var vlConfig = builder.Configuration.GetSection("VlAnalyzer");
var vlOptions = vlConfig.Get<VlAnalyzerOptions>() ?? new VlAnalyzerOptions();
app.Logger.LogInformation("VL Analyzer: Configured={IsConfigured}, Model={Model}, BaseUrl={BaseUrl}, ApiKey={ApiKeyStatus}",
    vlOptions.IsConfigured, vlOptions.Model, vlOptions.BaseUrl,
    string.IsNullOrEmpty(vlOptions.ApiKey) ? "(empty - service disabled)" : SensitiveDataMasker.MaskApiKey(vlOptions.ApiKey));

app.Logger.LogInformation("Student Service: BaseUrl={Url}", studentBaseUrl);

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

app.MapMistakeHttpEndpoints();
app.MapGet("/", () => "Mistake WebAPI host is running.");

app.Run();
