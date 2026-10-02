using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using Serilog;
using Ruoyu.Study.Common.Ai;
using Ruoyu.Study.Common.Authentication;
using Ruoyu.Study.Common.Oss;
using Ruoyu.Study.Mistake.Database;
using Ruoyu.Study.Mistake.Database.Repositories;
using Ruoyu.Study.Mistake.Domain.Repositories;
using Ruoyu.Study.Mistake.Domain.Services;
using Ruoyu.Study.Mistake.Host.Services;
using Ruoyu.Study.Mistake.Host.Services.Intake;
using Ruoyu.Study.Mistake.Service.Intake;
using Ruoyu.Study.Mistake.Host.Services.Health;
using Ruoyu.Study.Mistake.Host.Services.VlImageAnalysis;
using Ruoyu.Study.Mistake.Host;
using Ruoyu.Study.Mistake.Service;
using Ruoyu.Study.Consul.Shared;
using Ruoyu.Study.Mistake.StudentClient;
using Ruoyu.Study.Mistake.Service.StudentIntegration;

var builder = WebApplication.CreateBuilder(args);

var modeValue = builder.Configuration["Mistake:ConfigMode"];
var localMode = string.Equals(modeValue, "Local", StringComparison.OrdinalIgnoreCase);
if (modeValue is not null && !localMode &&
    !string.Equals(modeValue, "Platform", StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("Mistake:ConfigMode must be Local or Platform.");

if (localMode)
{
    // Avoid reading the platform Serilog:WriteTo array: it contains a Loki sink.
    builder.Host.UseSerilog((_, _, logger) => logger
        .MinimumLevel.Information()
        .Enrich.FromLogContext()
        .WriteTo.Console());
}
else
{
    builder.Configuration.AddRuoyuConsulConfiguration(builder.Configuration);
    builder.Configuration.AddRuoyuLokiSink();
    builder.Host.UseRuoyuSerilog("Ruoyu.Study.Mistake");
}

var consulOptions = localMode ? null : RuoyuConsulOptions.Bind(builder.Configuration);
var consulRuntimeState = localMode ? null : RuoyuConsulRuntimeState.Instance;

bool ReadWorkerEnabled(string key)
{
    var value = builder.Configuration[key];
    if (value is null) return !localMode;
    if (bool.TryParse(value, out var enabled)) return enabled;
    throw new InvalidOperationException($"{key} must be true or false.");
}

// Resolve all switches after Platform Consul configuration and before database access
// or any hosted service can start. Values are fixed for this process lifetime.
var uploadPollingEnabled = ReadWorkerEnabled("Mistake:Workers:UploadPollingWorker:Enabled");
var reanalyzeJobEnabled = ReadWorkerEnabled("Mistake:Workers:ReanalyzeJobHostedService:Enabled");
var reanalyzeCleanupEnabled = ReadWorkerEnabled("Mistake:Workers:ReanalyzeJobCleanupService:Enabled");

// Student capability switch. Local defaults to disabled so an unwired host fails
// closed instead of writing to an implicit Student endpoint; Platform keeps the
// current enabled default. Explicit values only accept booleans.
const string studentEnabledKey = "Mistake:Student:Enabled";
var studentEnabledValue = builder.Configuration[studentEnabledKey];
bool studentEnabled;
if (studentEnabledValue is null)
    studentEnabled = !localMode;
else if (bool.TryParse(studentEnabledValue, out var parsedStudentEnabled))
    studentEnabled = parsedStudentEnabled;
else
    throw new InvalidOperationException($"{studentEnabledKey} must be true or false.");

const string uploadPollingEnabledKey = "Mistake:Workers:UploadPollingWorker:Enabled";
if (!studentEnabled && uploadPollingEnabled)
    throw new InvalidOperationException(
        $"{studentEnabledKey}=false cannot be combined with {uploadPollingEnabledKey}=true; the upload polling worker requires the Student capability.");

// Inbound /api authentication (IKJEIC): shared JWT trust with an anonymous
// compatibility window. strict=false (default) keeps every /api endpoint
// reachable without a token; valid tokens still populate HttpContext.User.
// strict=true requires an authenticated caller (shared audience or Admin
// AppId audience scheme) on /api only, and the trust snapshot must be
// complete. The value is fixed for this process lifetime.
const string strictApiAuthKey = "IdentityService:MistakeApiStrictAuth";
var strictApiAuthValue = builder.Configuration[strictApiAuthKey];
bool strictApiAuth;
if (strictApiAuthValue is null)
    strictApiAuth = false;
else if (bool.TryParse(strictApiAuthValue, out var parsedStrictApiAuth))
    strictApiAuth = parsedStrictApiAuth;
else
    throw new InvalidOperationException($"{strictApiAuthKey} must be true or false.");

// Per-resource authorization switch (IKJEPZ): an additional layer on top of
// strict authentication, default off. It presupposes an authenticated caller,
// so combining it with the anonymous compatibility window fails startup.
const string resourceApiAuthKey = "IdentityService:MistakeApiResourceAuth";
var resourceApiAuthValue = builder.Configuration[resourceApiAuthKey];
bool resourceApiAuth;
if (resourceApiAuthValue is null)
    resourceApiAuth = false;
else if (bool.TryParse(resourceApiAuthValue, out var parsedResourceApiAuth))
    resourceApiAuth = parsedResourceApiAuth;
else
    throw new InvalidOperationException($"{resourceApiAuthKey} must be true or false.");
if (resourceApiAuth && !strictApiAuth)
    throw new InvalidOperationException(
        $"{resourceApiAuthKey}=true requires {strictApiAuthKey}=true; per-resource authorization presupposes an authenticated caller.");

var intakeValue = builder.Configuration["Mistake:SourceIntake:Enabled"];
var intakeEnabled = intakeValue is null ? false : bool.TryParse(intakeValue, out var parsedIntake)
    ? parsedIntake : throw new InvalidOperationException("Mistake:SourceIntake:Enabled must be true or false.");
if (intakeEnabled && (!studentEnabled || !strictApiAuth || !resourceApiAuth))
    throw new InvalidOperationException("Source intake requires Student, strict API authentication and resource authorization.");
builder.Services.AddSingleton(new SourceIntakeOptions(intakeEnabled));
var targetValue=builder.Configuration["Mistake:SourceTargets:Enabled"];
var targetsEnabled=targetValue is null ? false : bool.TryParse(targetValue,out var parsedTargets)
    ? parsedTargets : throw new InvalidOperationException("Mistake:SourceTargets:Enabled must be true or false.");
if(targetsEnabled && (!strictApiAuth || !resourceApiAuth))
    throw new InvalidOperationException("Source targets require strict API authentication and resource authorization.");
builder.Services.AddSingleton(new SourceTargetOptions(targetsEnabled));
var returnValue=builder.Configuration["Mistake:SourceReturns:Enabled"];
var returnsEnabled=returnValue is null ? false : bool.TryParse(returnValue,out var parsedReturns)
    ? parsedReturns : throw new InvalidOperationException("Mistake:SourceReturns:Enabled must be true or false.");
if(returnsEnabled && (!studentEnabled || !strictApiAuth || !resourceApiAuth))
    throw new InvalidOperationException("Source returns require Student, strict API authentication and resource authorization.");
builder.Services.AddSingleton(new SourceReturnOptions(returnsEnabled));


var identityTrustOptions = builder.Configuration
    .GetSection(IdentityAuthenticationOptions.SectionName)
    .Get<IdentityAuthenticationOptions>() ?? new IdentityAuthenticationOptions();
var identityTrustValidation = new IdentityAuthenticationOptionsValidator()
    .Validate(Options.DefaultName, identityTrustOptions);
if (strictApiAuth && identityTrustValidation.Failed)
{
    // Strict mode with a missing or incomplete trust snapshot fails startup
    // with configuration-key-only messages instead of serving a silent
    // all-401 surface. The compatibility window (strict=false) does not
    // validate: an unconfigured or invalid section simply stays anonymous.
    throw new OptionsValidationException(
        IdentityAuthenticationOptions.SectionName,
        typeof(IdentityAuthenticationOptions),
        identityTrustValidation.Failures);
}
var identityTrustConfigured = identityTrustValidation.Succeeded;
if (identityTrustConfigured)
{
    builder.Services.AddRuoyuJwtBearer(
        builder.Configuration,
        builder.Environment,
        configureAuthorization: options =>
        {
            // Compatibility window: anonymous callers keep reaching /api, so
            // the shared RequireAuthenticatedUser fallback policy is turned
            // off. Strict mode enforces the requirement on the /api group
            // itself; the root endpoint and future health endpoints stay
            // anonymous in both modes.
            options.FallbackPolicy = null;
            MistakeApiAuthorization.AddMistakeApiPolicy(options);
        });
    // Optional Admin AppId audience scheme (shared library, IKJ8PJ failure
    // semantics): unconfigured fails closed for that audience only.
    builder.Services.AddRuoyuAdminAudienceJwtBearer(builder.Configuration);
}

// Kestrel has one HTTP/1 listener. Resolve the final value after Platform
// configuration is loaded; URLS and Endpoints:Http are not listener authorities.
const string httpPortKey = "Mistake:Http:Port";
var configuredHttpPort = builder.Configuration[httpPortKey];
var httpPort = configuredHttpPort is null ? 5007 :
    int.TryParse(configuredHttpPort, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedHttpPort) &&
    parsedHttpPort is >= 1 and <= 65535
        ? parsedHttpPort
        : throw new InvalidOperationException($"{httpPortKey} must be an integer from 1 to 65535.");

builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(httpPort, listenOptions =>
    {
        listenOptions.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http1;
    });
});

// An enabled source intake must validate its credential and HTTPS snapshot even for explicit upgrade commands.
var intakeSettings = (intakeEnabled || returnsEnabled) ? ResolveStudentSettings() : ((string Url, string Source, StudentServiceAuthenticationOptions Authentication)?)null;

string? connectionString;
if (localMode)
{
    connectionString = builder.Configuration.GetConnectionString("MistakeLocal");
    if (string.IsNullOrWhiteSpace(connectionString))
        throw new InvalidOperationException("Local mode requires ConnectionStrings:MistakeLocal.");
    try
    {
        var local = new NpgsqlConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(local.Host) || string.IsNullOrWhiteSpace(local.Database) ||
            string.IsNullOrWhiteSpace(local.Username))
            throw new ArgumentException("Missing connection fields.");
        if (string.Equals(local.Host, "localhost", StringComparison.OrdinalIgnoreCase) &&
            local.Port == 5432 && local.Database == "ruoyu_study_mistake" &&
            local.Username == "phil" && string.IsNullOrEmpty(local.Password))
            throw new ArgumentException("Example connection selected.");
    }
    catch (ArgumentException)
    {
        throw new InvalidOperationException("Local mode requires a valid, explicit MistakeLocal PostgreSQL connection distinct from the platform example.");
    }
    var connectionSource = Environment.GetEnvironmentVariable("ConnectionStrings__MistakeLocal") is null
        ? "File" : "Environment";
    Console.WriteLine($"Mistake configuration: Mode=Local, DatabaseConnectionSource={connectionSource}.");
}
else
{
    connectionString = SharedPostgreSqlConnectionStringFactory.BuildOrFallback(
        builder.Configuration, builder.Configuration.GetConnectionString("Default"));
    Console.WriteLine("Mistake configuration: Mode=Platform, DatabaseConnectionSource=PlatformOrFallback.");
}
if (args.Length == 1 && args[0] == "--upgrade-empty-db")
{
    var options = new DbContextOptionsBuilder<MistakeDbContext>().UseNpgsql(connectionString).Options;
    await using var upgradeContext = new MistakeDbContext(options);
    await DatabaseInitializer.UpgradeEmptyAsync(upgradeContext);
    Console.WriteLine("Mistake empty database upgrade verified.");
    return;
}
if (args.Contains("--upgrade-known-db", StringComparer.Ordinal))
{
    var databaseIndex = Array.IndexOf(args, "--database");
    var expectedDatabase = databaseIndex >= 0 && databaseIndex + 1 < args.Length ? args[databaseIndex + 1] : "";
    var options = new DbContextOptionsBuilder<MistakeDbContext>().UseNpgsql(connectionString).Options;
    await using var upgradeContext = new MistakeDbContext(options);
    await DatabaseInitializer.UpgradeKnownAsync(upgradeContext, expectedDatabase,
        args.Contains("--writes-stopped", StringComparer.Ordinal),
        args.Contains("--backup-verified", StringComparer.Ordinal));
    Console.WriteLine("Mistake known database upgrade verified.");
    return;
}
// Local runtime must resolve its own OSS settings before database initialization
// or any hosted service can contact Student or S3. Upgrade commands return above.
var ossConfig = new OssOptions();
if (localMode)
    ossConfig = LocalOssConfiguration.Read(builder.Configuration);
else
    builder.Configuration.GetSection("Oss").Bind(ossConfig);

builder.Services.AddDbContext<MistakeDbContext>(options =>
{
    options.UseNpgsql(connectionString);
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
builder.Services.AddSingleton<IOssService>(sp =>
{
    return new S3OssService(ossConfig, new[] { "mistakes/", "uploads/" });
});

// VL Analyzer for image grouping
builder.Services.Configure<VlAnalyzerOptions>(builder.Configuration.GetSection("VlAnalyzer"));
// Readiness snapshot input (IKJFIX): which optional capabilities this process
// wired. The snapshot is fixed at startup like every other switch; the
// readiness probe never reports components that are not part of this process.
var vlAnalyzerOptions = builder.Configuration.GetSection("VlAnalyzer").Get<VlAnalyzerOptions>() ?? new VlAnalyzerOptions();
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
if (uploadPollingEnabled) builder.Services.AddHostedService<UploadPollingWorker>();

// Reanalyze job workers: process AI re-analysis jobs asynchronously.
// MaxConcurrency is hardcoded to 2 inside the service (do not externalize).
if (reanalyzeJobEnabled) builder.Services.AddHostedService<ReanalyzeJobHostedService>();
if (reanalyzeCleanupEnabled) builder.Services.AddHostedService<ReanalyzeJobCleanupService>();

// Student HTTP client (used by polling worker and review flow).
// The capability state is fixed for this process lifetime; re-enabling
// requires a restart with valid configuration.
builder.Services.AddSingleton(new StudentCapabilityOptions { Enabled = studentEnabled });
var (studentBaseUrl, studentAddressSource, studentServiceAuthentication) = intakeSettings ?? ResolveStudentSettings();
builder.Services.AddHttpClient<SourceSnapshotClient>(client =>
{
    client.BaseAddress = new Uri(studentBaseUrl);
    client.Timeout = TimeSpan.FromSeconds(35);
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })
  .AddHttpMessageHandler<StudentServiceTokenHandler>();
builder.Services.AddHttpClient<SourceReturnClient>(client =>
{
    client.BaseAddress = new Uri(studentBaseUrl);
    client.Timeout = TimeSpan.FromSeconds(35);
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })
  .AddHttpMessageHandler<StudentServiceTokenHandler>();
builder.Services.AddScoped<SourceIntakeService>();
builder.Services.AddScoped<ISourceIntakeService>(sp => sp.GetRequiredService<SourceIntakeService>());
builder.Services.AddScoped<IMistakeSourceGuard>(sp => sp.GetRequiredService<SourceIntakeService>());
builder.Services.AddScoped<ISourceTargetService>(sp => sp.GetRequiredService<SourceIntakeService>());
if(targetsEnabled) builder.Services.AddHostedService<SourceTargetWorker>();
builder.Services.AddScoped<ISourceReturnService>(sp => sp.GetRequiredService<SourceIntakeService>());
if(returnsEnabled) builder.Services.AddHostedService<SourceReturnWorker>();
builder.Services.AddSingleton(studentServiceAuthentication);
builder.Services.AddTransient<StudentServiceTokenHandler>();
builder.Services.AddStudentHttpClient(studentBaseUrl);
builder.Services.AddHttpClient<IStudentHttpClient, StudentHttpClient>()
    .AddHttpMessageHandler<StudentServiceTokenHandler>();
builder.Services.AddScoped<IUploadSourceGateway, StudentUploadSourceGateway>();

// Readiness dependency matrix (IKJFIX): registered after every capability
// switch and address is resolved so the snapshot reflects the wired process.
builder.Services.AddSingleton(new MistakeReadinessSnapshot(
    studentEnabled, studentBaseUrl, vlAnalyzerOptions.IsConfigured, vlAnalyzerOptions.BaseUrl));
builder.Services.AddHttpClient(MistakeReadinessProbe.HttpClientName)
    .ConfigureHttpClient(client => client.Timeout = MistakeReadinessProbe.ProbeTimeout);
builder.Services.AddScoped<MistakeReadinessProbe>();

// Per-resource authorization (IKJEPZ): startup snapshot like every identity
// trust setting; Enabled=false keeps every /api endpoint byte-identical to
// the compatibility-window baseline because the endpoints skip the checks.
builder.Services.AddSingleton(new MistakeApiResourceAuthOptions { Enabled = resourceApiAuth });
builder.Services.AddScoped<IMistakeApiResourceAuthorizationService, MistakeApiResourceAuthorizationService>();

var app = builder.Build();

app.Logger.LogInformation("Mistake Service starting");
app.Logger.LogInformation("Endpoint: HTTP={HttpPort}", httpPort);
if (consulOptions is not null && consulRuntimeState is not null)
    app.Logger.LogInformation(
        "Consul startup diagnostics: Address={Address}, Token={Token}, Source={Source}, KeyCount={KeyCount}, Prefixes={Prefixes}, LastError={LastError}",
        $"{consulOptions.Host}:{consulOptions.Port}",
        StartupDiagnosticsFormatter.MaskSecret(consulOptions.Token),
        consulRuntimeState.Source,
        consulRuntimeState.KeyCount,
        StartupDiagnosticsFormatter.SummarizePrefixes(consulRuntimeState.LoadedPrefixes),
        StartupDiagnosticsFormatter.SummarizeError(consulRuntimeState.LastError));
if (!string.IsNullOrEmpty(connectionString))
{
    var csb = new DbConnectionStringBuilder { ConnectionString = connectionString };
    app.Logger.LogInformation("Database: PostgreSQL {Host}:{Port}/{Database}", csb["Host"], csb.TryGetValue("Port", out var dbPort) ? dbPort : "5432", csb["Database"]);
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

var vlOptions = vlAnalyzerOptions;
app.Logger.LogInformation("VL Analyzer: Configured={IsConfigured}, Model={Model}, BaseUrl={BaseUrl}, ApiKey={ApiKeyStatus}",
    vlOptions.IsConfigured, vlOptions.Model, vlOptions.BaseUrl,
    string.IsNullOrEmpty(vlOptions.ApiKey) ? "(empty - service disabled)" : SensitiveDataMasker.MaskApiKey(vlOptions.ApiKey));

app.Logger.LogInformation(
    "Student capability: Enabled={Enabled}, Mode={Mode}, AddressSource={AddressSource}, BaseUrl={BaseUrl}",
    studentEnabled, localMode ? "Local" : "Platform", studentAddressSource,
    studentEnabled ? studentBaseUrl : "(capability disabled)");

app.Logger.LogInformation(
    "Inbound API auth: Strict={Strict}, ResourceAuth={ResourceAuth}, TrustConfigured={TrustConfigured}, Authority={Authority}, Audience={Audience}, AdminAudienceConfigured={AdminAudienceConfigured}",
    strictApiAuth, resourceApiAuth, identityTrustConfigured,
    identityTrustConfigured ? identityTrustOptions.Authority : "(not configured)",
    identityTrustConfigured ? identityTrustOptions.Audience : "(not configured)",
    identityTrustOptions.GetAdminAudience() is not null);

app.Logger.LogInformation("OSS: Endpoint={Endpoint}, Bucket={Bucket}, Prefixes=mistakes/,uploads/",
    ossConfig.InternalEndpoint ?? "N/A", ossConfig.BucketName ?? "N/A");
app.Logger.LogInformation("UploadPollingWorker: Enabled={Enabled}, PollInterval=60s", uploadPollingEnabled);
app.Logger.LogInformation("ReanalyzeJobHostedService: Enabled={Enabled}, MaxConcurrency=2 (hardcoded)", reanalyzeJobEnabled);
app.Logger.LogInformation("ReanalyzeJobCleanupService: Enabled={Enabled}, RetentionDays=30 (hardcoded)", reanalyzeCleanupEnabled);

using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<MistakeDbContext>();
    if (dbContext.Database.IsRelational())
    {
        var loggerFactory = scope.ServiceProvider.GetRequiredService<ILoggerFactory>();
        await DatabaseInitializer.InitializeAsync(dbContext, loggerFactory);
    }
    else
    {
        // Non-relational providers are only used by the WebApplicationFactory
        // test pipeline; the read-only PostgreSQL verification does not apply.
        await dbContext.Database.EnsureCreatedAsync();
    }
}

app.MapMistakeHttpEndpoints(requireAuthenticatedApi: strictApiAuth);
// Health endpoints (IKJFIX): anonymous in both modes, outside the /api group.
app.MapMistakeHealthEndpoints();
app.MapGet("/", () => "Mistake WebAPI host is running.");

app.Run();

(string Url, string Source, StudentServiceAuthenticationOptions Authentication) ResolveStudentSettings()
{
    string studentBaseUrl;
    string studentAddressSource;
    if (!studentEnabled)
    {
        // Keep the typed client registered so existing domain operations keep
        // working, but point it at a reserved, never-resolving placeholder so the
        // disabled host cannot send any request to Student.
        studentBaseUrl = "http://student-capability-disabled.invalid";
        studentAddressSource = "Disabled";
    }
    else if (localMode)
    {
        // Local enabled mode requires the dedicated explicit base URL. It never
        // falls back to StudentService:Url/BaseUrl/GrpcUrl or the localhost
        // example, not even when those keys are present in appsettings.json.
        const string localStudentUrlKey = "Mistake:Local:Student:Url";
        var localStudentUrl = builder.Configuration[localStudentUrlKey];
        if (string.IsNullOrWhiteSpace(localStudentUrl))
            throw new InvalidOperationException(
                $"{localStudentUrlKey} is required when {studentEnabledKey}=true in Local mode; " +
                "StudentService:Url/BaseUrl/GrpcUrl and the localhost example are not accepted here.");
        if (!Uri.TryCreate(localStudentUrl, UriKind.Absolute, out var localStudentUri) ||
            (localStudentUri.Scheme != Uri.UriSchemeHttp && localStudentUri.Scheme != Uri.UriSchemeHttps) ||
            localStudentUri.Host.Length == 0 || localStudentUri.UserInfo.Length != 0 ||
            localStudentUri.AbsolutePath != "/" ||
            localStudentUri.Query.Length != 0 || localStudentUri.Fragment.Length != 0 ||
            localStudentUrl.Any(char.IsWhiteSpace))
            throw new InvalidOperationException(
                $"{localStudentUrlKey} must be an absolute HTTP(S) base URL without credentials, path, query or fragment.");
        studentBaseUrl = localStudentUrl;
        studentAddressSource = "Explicit";
    }
    else
    {
        // Platform keeps the legacy fallback chain unchanged.
        studentBaseUrl = builder.Configuration["StudentService:Url"]
                         ?? builder.Configuration["StudentService:BaseUrl"]
                         ?? builder.Configuration["StudentService:GrpcUrl"]
                         ?? "http://localhost:5005";
        studentAddressSource = "Platform";
    }
    var studentServiceAuthentication = StudentServiceAuthenticationOptions.Read(builder.Configuration, studentBaseUrl);
    if (studentServiceAuthentication.Enabled && !studentEnabled)
        throw new InvalidOperationException("Mistake:Student:ServiceAuthentication:Enabled requires Mistake:Student:Enabled.");
    if ((intakeEnabled || returnsEnabled) && (!studentServiceAuthentication.Enabled ||
        !Uri.TryCreate(studentBaseUrl, UriKind.Absolute, out var intakeStudentUri) || intakeStudentUri.Scheme != "https"))
        throw new InvalidOperationException(intakeEnabled ? "Source intake requires RS256 Student service credentials and HTTPS." : "Source returns require RS256 Student service credentials and HTTPS.");
    return (studentBaseUrl, studentAddressSource, studentServiceAuthentication);
}

// Make Program class public for integration tests (WebApplicationFactory<Program>)
public partial class Program;
