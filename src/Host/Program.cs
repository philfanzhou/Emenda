using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using Serilog;
using Emenda.Common.Ai;
using Emenda.Common.Authentication;
using Emenda.Common.Oss;
using Emenda.Database;
using Emenda.Database.Repositories;
using Emenda.Domain.Repositories;
using Emenda.Domain.Services;
using Emenda.Host.Services;
using Emenda.Host.Services.Intake;
using Emenda.Host.Services.Layout;
using Emenda.Service.Intake;
using Emenda.Host.Services.Health;
using Emenda.Host.Services.VlImageAnalysis;
using Emenda.Host;
using Emenda.Host.Security;
using Emenda.Service;
using Emenda.Consul;
using Emenda.StudentClient;
using Emenda.Service.StudentIntegration;

var builder = WebApplication.CreateBuilder(args);

// Product standalone mode: standard configuration only; the product does not
// read Consul, does not require Loki and does not use the platform identity
// trust. Legacy Local/Platform modes stay available for compatibility.
var standalone = builder.Configuration.IsStandaloneProductMode();

var modeValue = builder.Configuration["Mistake:ConfigMode"];
var localMode = !standalone && string.Equals(modeValue, "Local", StringComparison.OrdinalIgnoreCase);
if (!standalone && modeValue is not null && !localMode &&
    !string.Equals(modeValue, "Platform", StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("Mistake:ConfigMode must be Local or Platform.");

if (standalone)
{
    // Standalone deployments log to the console; no platform Loki sink.
    builder.Host.UseSerilog((_, _, logger) => logger
        .MinimumLevel.Information()
        .Enrich.FromLogContext()
        .WriteTo.Console());
}
else if (localMode)
{
    // Avoid reading the platform Serilog:WriteTo array: it contains a Loki sink.
    builder.Host.UseSerilog((_, _, logger) => logger
        .MinimumLevel.Information()
        .Enrich.FromLogContext()
        .WriteTo.Console());
}
else
{
    builder.Configuration.AddEmendaConsulConfiguration(builder.Configuration);
    builder.Configuration.AddEmendaLokiSink();
    builder.Host.UseEmendaSerilog("Emenda");
}

var consulOptions = standalone || localMode ? null : EmendaConsulOptions.Bind(builder.Configuration);
var consulRuntimeState = standalone || localMode ? null : EmendaConsulRuntimeState.Instance;

bool ReadWorkerEnabled(string key)
{
    var value = builder.Configuration[key];
    if (value is null)
        return standalone
            ? !key.EndsWith("UploadPollingWorker:Enabled", StringComparison.Ordinal)
            : !localMode;
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
    studentEnabled = standalone ? false : !localMode;
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
if (standalone && (strictApiAuth || resourceApiAuth))
    throw new InvalidOperationException(
        "Standalone mode uses product caller credentials; " +
        $"{strictApiAuthKey} and {resourceApiAuthKey} must not be enabled in this mode.");

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
var legacyReturnValue = builder.Configuration["Mistake:LegacyReturns:Enabled"];
var legacyReturnsEnabled = legacyReturnValue is null ? false : bool.TryParse(legacyReturnValue, out var parsedLegacyReturns)
    ? parsedLegacyReturns : throw new InvalidOperationException("Mistake:LegacyReturns:Enabled must be true or false.");
if (legacyReturnsEnabled && (!studentEnabled || !strictApiAuth || !resourceApiAuth))
    throw new InvalidOperationException("Legacy returns require Student, strict API authentication and resource authorization.");
var handoffValue=builder.Configuration["Mistake:SourceHandoffs:Enabled"];
var handoffsEnabled=handoffValue is null ? false : bool.TryParse(handoffValue,out var parsedHandoffs)
    ? parsedHandoffs : throw new InvalidOperationException("Mistake:SourceHandoffs:Enabled must be true or false.");
if(handoffsEnabled && (!studentEnabled || !strictApiAuth || !resourceApiAuth))
    throw new InvalidOperationException("Source handoffs require Student, strict API authentication and resource authorization.");
builder.Services.AddSingleton(new SourceHandoffOptions(handoffsEnabled));
// Product runtime shape: a standalone deployment has no Student service, so
// review rejection is persisted locally instead of using the return flow.
builder.Services.AddSingleton(new ProductRuntimeOptions(standalone));

// StructaDoc layout capability switch (IKQA2N): same startup-snapshot family as the
// switches above. Default off; disabled configuration makes zero layout requests
// and existing processing is unchanged. The value is fixed for this process lifetime.
const string layoutEnabledKey = "Mistake:Layout:Enabled";
var layoutValue = builder.Configuration[layoutEnabledKey];
var layoutEnabled = layoutValue is null ? false : bool.TryParse(layoutValue, out var parsedLayout)
    ? parsedLayout : throw new InvalidOperationException($"{layoutEnabledKey} must be true or false.");
builder.Services.AddSingleton(new LayoutCapabilityOptions(layoutEnabled));
var structaDocLayoutOptions = builder.Configuration.GetSection(StructaDocLayoutOptions.SectionName)
    .Get<StructaDocLayoutOptions>() ?? new StructaDocLayoutOptions();


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
if (!standalone && identityTrustConfigured)
{
    builder.Services.AddEmendaJwtBearer(
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
    builder.Services.AddEmendaAdminAudienceJwtBearer(builder.Configuration);
}

if (standalone)
{
    builder.Services.AddProductCallerAuthentication(builder.Configuration);
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
var intakeSettings = (intakeEnabled || returnsEnabled || handoffsEnabled || legacyReturnsEnabled) ? ResolveStudentSettings() : ((string Url, string Source, StudentServiceAuthenticationOptions Authentication)?)null;

string? connectionString;
if (standalone)
{
    connectionString = builder.Configuration.GetConnectionString("Emenda")
        ?? builder.Configuration.GetConnectionString("Default");
    if (string.IsNullOrWhiteSpace(connectionString))
        throw new InvalidOperationException(
            "Standalone mode requires ConnectionStrings:Emenda (or ConnectionStrings:Default).");
    var connectionSource = Environment.GetEnvironmentVariable("ConnectionStrings__Emenda") is null
        ? "File" : "Environment";
    Console.WriteLine($"Emenda configuration: Mode=Standalone, DatabaseConnectionSource={connectionSource}.");
}
else if (localMode)
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
// Normal startup upgrades the database itself. The two commands are kept for existing scripts:
// both run the same upgrade and exit. --writes-stopped and --backup-verified are accepted but no
// longer required; an explicit --database must still match the connected database.
var emptyCommand = args.Length == 1 && args[0] == "--upgrade-empty-db";
var knownCommand = args.Contains("--upgrade-known-db", StringComparer.Ordinal);
if (emptyCommand || knownCommand)
{
    string? expectedDatabase = null;
    var databaseIndex = knownCommand ? Array.IndexOf(args, "--database") : -1;
    if (databaseIndex >= 0)
        expectedDatabase = databaseIndex + 1 < args.Length ? args[databaseIndex + 1] : "";
    var options = new DbContextOptionsBuilder<MistakeDbContext>().UseNpgsql(connectionString).Options;
    await using var upgradeContext = new MistakeDbContext(options);
    var upgrade = await DatabaseInitializer.UpgradeToTargetAsync(upgradeContext, expectedDatabase);
    Console.WriteLine(upgrade.Upgraded
        ? $"Mistake database upgraded from version {upgrade.FromVersion} to target version {upgrade.ToVersion}; steps: {string.Join(", ", upgrade.Steps)}."
        : $"Mistake database already at target version {upgrade.ToVersion}; nothing written.");
    Console.WriteLine(emptyCommand ? "Mistake empty database upgrade verified." : "Mistake known database upgrade verified.");
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
builder.Services.AddSingleton<Emenda.Service.Services.IVlAnalyzer, VlUploadAnalyzer>();

// Polling worker: polls Student service for Processing records and processes them
if (uploadPollingEnabled) builder.Services.AddHostedService<UploadPollingWorker>();

// StructaDoc layout detection client (IKQA2N). The capability state is fixed for this
// process lifetime. Disabled keeps the typed client registered but points it at a
// never-resolving placeholder, and the client itself fails closed before any HTTP
// exchange, so a disabled host cannot send any layout request. Enabled requires the
// administrator-configured base URL and the scoped deployment credential, resolved
// before any database access or hosted service can start.
string layoutBaseUrl;
if (!layoutEnabled)
{
    layoutBaseUrl = StructaDocLayoutClient.DisabledPlaceholderBaseUrl;
}
else
{
    if (string.IsNullOrWhiteSpace(structaDocLayoutOptions.BaseUrl))
        throw new InvalidOperationException(
            $"{StructaDocLayoutOptions.SectionName}:BaseUrl is required when {layoutEnabledKey}=true.");
    if (string.IsNullOrWhiteSpace(structaDocLayoutOptions.ApiKey))
        throw new InvalidOperationException(
            $"{StructaDocLayoutOptions.SectionName}:ApiKey is required when {layoutEnabledKey}=true.");
    if (!Uri.TryCreate(structaDocLayoutOptions.BaseUrl, UriKind.Absolute, out var layoutUri) ||
        (layoutUri.Scheme != Uri.UriSchemeHttp && layoutUri.Scheme != Uri.UriSchemeHttps) ||
        layoutUri.Host.Length == 0 || layoutUri.UserInfo.Length != 0 ||
        layoutUri.AbsolutePath != "/" ||
        layoutUri.Query.Length != 0 || layoutUri.Fragment.Length != 0 ||
        structaDocLayoutOptions.BaseUrl.Any(char.IsWhiteSpace))
        throw new InvalidOperationException(
            $"{StructaDocLayoutOptions.SectionName}:BaseUrl must be an absolute HTTP(S) base URL without credentials, path, query or fragment.");
    var layoutValidationErrors = structaDocLayoutOptions.Validate();
    if (layoutValidationErrors.Count > 0)
        throw new InvalidOperationException(string.Join(" ", layoutValidationErrors));
    layoutBaseUrl = structaDocLayoutOptions.BaseUrl;
}
builder.Services.AddHttpClient(StructaDocLayoutClient.HttpClientName, client =>
{
    client.BaseAddress = new Uri(layoutBaseUrl);
    // The configurable per-image budget (default 180 s) governs each exchange. The
    // HttpClient timeout is infinite so the framework's 100-second default can never
    // cut a cloud exchange whose own deadline is 120 seconds after its execution slot.
    client.Timeout = Timeout.InfiniteTimeSpan;
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddSingleton(sp =>
{
    var httpClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient(StructaDocLayoutClient.HttpClientName);
    return new StructaDocLayoutClient(
        httpClient,
        sp.GetRequiredService<LayoutCapabilityOptions>(),
        structaDocLayoutOptions,
        sp.GetRequiredService<ILogger<StructaDocLayoutClient>>());
});
builder.Services.AddSingleton<ILayoutDetectionClient>(sp => sp.GetRequiredService<StructaDocLayoutClient>());
builder.Services.AddSingleton(structaDocLayoutOptions);

// Layout jobs (IKQA2O): the enqueue command (request-key replay over an existing managed
// source) and the durable execution worker. The worker follows the capability switch —
// registered only when layout detection is enabled, so turning it off (a restart with
// Mistake:Layout:Enabled=false) refuses new work and stops processing while jobs,
// accepted results and read access are retained; queued and parked jobs resume when
// work is accepted again.
builder.Services.AddScoped<LayoutJobService>();
builder.Services.AddScoped<ILayoutJobService>(sp => sp.GetRequiredService<LayoutJobService>());
// Administrative layout operations (IKQA2P): the HTTP surface consumes the service-side
// adapter over the enqueue command and durable read views; it is registered regardless
// of the switch (replay and reads stay available, new work is refused while disabled).
builder.Services.AddScoped<LayoutJobAdminService>();
builder.Services.AddScoped<ILayoutJobAdminService>(sp => sp.GetRequiredService<LayoutJobAdminService>());
if (layoutEnabled) builder.Services.AddHostedService<LayoutJobHostedService>();

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
}).RemoveAllLoggers().ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })
  .AddHttpMessageHandler<StudentServiceTokenHandler>();
builder.Services.AddHttpClient<SourceHandoffClient>(client =>
{
    client.BaseAddress=new Uri(studentBaseUrl);client.Timeout=TimeSpan.FromSeconds(35);
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler {AllowAutoRedirect=false})
  .AddHttpMessageHandler<StudentServiceTokenHandler>();
if (legacyReturnsEnabled)
{
    builder.Services.AddSingleton(new LegacyReturnOptions(true));
    builder.Services.AddScoped<ILegacyReturnService, LegacyReturnService>();
    builder.Services.AddHostedService<LegacyReturnWorker>();
}
else
    builder.Services.AddScoped<ILegacyReturnService, DisabledLegacyReturnService>();
builder.Services.AddScoped<SourceIntakeService>();
builder.Services.AddScoped<ISourceIntakeService>(sp => sp.GetRequiredService<SourceIntakeService>());
builder.Services.AddScoped<IMistakeSourceGuard>(sp => sp.GetRequiredService<SourceIntakeService>());
builder.Services.AddScoped<ISourceTargetService>(sp => sp.GetRequiredService<SourceIntakeService>());
if(targetsEnabled) builder.Services.AddHostedService<SourceTargetWorker>();
builder.Services.AddScoped<ISourceReturnService>(sp => sp.GetRequiredService<SourceIntakeService>());
if(returnsEnabled) builder.Services.AddHostedService<SourceReturnWorker>();
builder.Services.AddScoped<ISourceHandoffService>(sp=>sp.GetRequiredService<SourceIntakeService>());
if(handoffsEnabled) builder.Services.AddHostedService<SourceHandoffWorker>();
builder.Services.AddScoped<ISourceReconciliationService, SourceReconciliationService>();
builder.Services.AddSingleton(studentServiceAuthentication);
builder.Services.AddTransient<StudentServiceTokenHandler>();
builder.Services.AddStudentHttpClient(studentBaseUrl);
builder.Services.AddHttpClient<IStudentHttpClient, StudentHttpClient>()
    .AddHttpMessageHandler<StudentServiceTokenHandler>();
builder.Services.AddScoped<IUploadSourceGateway, StudentUploadSourceGateway>();

// Product intake: the product-owned upload API and the shared legacy upload
// processor (the polling worker also resolves it from DI).
builder.Services.AddProductIntake();

// Readiness dependency matrix (IKJFIX): registered after every capability
// switch and address is resolved so the snapshot reflects the wired process.
builder.Services.AddSingleton(new MistakeReadinessSnapshot(
    studentEnabled, studentBaseUrl, vlAnalyzerOptions.IsConfigured, vlAnalyzerOptions.BaseUrl,
    strictApiAuth, identityTrustOptions.Authority, identityTrustOptions.Issuer,
    layoutEnabled, layoutEnabled ? layoutBaseUrl : string.Empty));
builder.Services.AddHttpClient(MistakeReadinessProbe.HttpClientName)
    .ConfigureHttpClient(client => client.Timeout = MistakeReadinessProbe.ProbeTimeout);
builder.Services.AddHttpClient(MistakeReadinessProbe.IdentityHttpClientName)
    .ConfigureHttpClient(client => client.Timeout = MistakeReadinessProbe.ProbeTimeout)
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
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
    var pool = new Npgsql.NpgsqlConnectionStringBuilder(connectionString);
    app.Logger.LogInformation("Database pool: {Pool}", StartupDiagnosticsFormatter.SummarizePostgreSqlPool(
        pool.MaxPoolSize, pool.MinPoolSize, pool.ConnectionIdleLifetime, pool.ConnectionPruningInterval, pool.Timeout, pool.Pooling));
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
    studentEnabled, standalone ? "Standalone" : localMode ? "Local" : "Platform", studentAddressSource,
    studentEnabled ? studentBaseUrl : "(capability disabled)");

app.Logger.LogInformation(
    "Layout detection: Enabled={Enabled}, BaseUrl={BaseUrl}, ApiKey={ApiKeyStatus}",
    layoutEnabled,
    layoutEnabled ? layoutBaseUrl : StructaDocLayoutClient.DisabledPlaceholderBaseUrl,
    layoutEnabled ? SensitiveDataMasker.MaskApiKey(structaDocLayoutOptions.ApiKey) : "(capability disabled)");
if (layoutEnabled)
{
    // Read back the actually registered client so the evidence is the wired timeout, not
    // the intended one. Creating a client sends no request.
    using var layoutHttpClient = app.Services.GetRequiredService<IHttpClientFactory>()
        .CreateClient(StructaDocLayoutClient.HttpClientName);
    app.Logger.LogInformation(
        "Layout HTTP budget: PerImageBudgetSeconds={Budget}, MaxConcurrentExchanges={Concurrency}, MaxResponseBytes={MaxResponseBytes}, HttpClientTimeout={Timeout}",
        structaDocLayoutOptions.PerImageBudgetSeconds,
        structaDocLayoutOptions.MaxConcurrentExchanges,
        structaDocLayoutOptions.MaxResponseBytes,
        layoutHttpClient.Timeout == Timeout.InfiniteTimeSpan ? "Infinite" : layoutHttpClient.Timeout.ToString());
}

app.Logger.LogInformation(
    "Inbound API auth: Strict={Strict}, ResourceAuth={ResourceAuth}, TrustConfigured={TrustConfigured}, Authority={Authority}, Audience={Audience}, AdminAudienceConfigured={AdminAudienceConfigured}",
    strictApiAuth, resourceApiAuth, identityTrustConfigured,
    identityTrustConfigured ? identityTrustOptions.Authority : "(not configured)",
    identityTrustConfigured ? identityTrustOptions.Audience : "(not configured)",
    identityTrustOptions.GetAdminAudiences().Count > 0);

if (standalone)
{
    var callerOptions = app.Services
        .GetRequiredService<IOptions<ProductCallerAuthenticationOptions>>().Value;
    app.Logger.LogInformation(
        "Product caller authentication: Enabled={Enabled}, AllowAnonymous={AllowAnonymous}, Callers={CallerCount}",
        callerOptions.Enabled, callerOptions.AllowAnonymous, callerOptions.ValidCallers.Count);
}

app.Logger.LogInformation("OSS: Endpoint={Endpoint}, Bucket={Bucket}, Prefixes=mistakes/,uploads/",
    ossConfig.InternalEndpoint ?? "N/A", ossConfig.BucketName ?? "N/A");
app.Logger.LogInformation("UploadPollingWorker: Enabled={Enabled}, PollInterval=60s", uploadPollingEnabled);
app.Logger.LogInformation("ReanalyzeJobHostedService: Enabled={Enabled}, MaxConcurrency=2 (hardcoded)", reanalyzeJobEnabled);
app.Logger.LogInformation("LayoutJobHostedService: Enabled={Enabled}, MaxConcurrency=2 (hardcoded)", layoutEnabled);
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
        // test pipeline; the PostgreSQL upgrade and verification do not apply.
        await dbContext.Database.EnsureCreatedAsync();
    }
}

if (standalone)
{
    // Product callers present a per-caller credential; health, contract and
    // root endpoints stay anonymous.
    app.UseMiddleware<ProductCallerAuthenticationMiddleware>();
}
app.MapMistakeHttpEndpoints(requireAuthenticatedApi: !standalone && strictApiAuth);
if (standalone)
{
    // Product-owned intake entry point; it lives under /api, so the product
    // caller credential middleware above protects it.
    app.MapProductIntakeEndpoints();
}
app.MapMistakeContractEndpoints();
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
    if ((intakeEnabled || returnsEnabled || handoffsEnabled || legacyReturnsEnabled) && (!studentServiceAuthentication.Enabled ||
        !Uri.TryCreate(studentBaseUrl, UriKind.Absolute, out var intakeStudentUri) || intakeStudentUri.Scheme != "https"))
        throw new InvalidOperationException(intakeEnabled ? "Source intake requires RS256 Student service credentials and HTTPS." : returnsEnabled ? "Source returns require RS256 Student service credentials and HTTPS." : handoffsEnabled ? "Source handoffs require RS256 Student service credentials and HTTPS." : "Legacy returns require RS256 Student service credentials and HTTPS.");
    return (studentBaseUrl, studentAddressSource, studentServiceAuthentication);
}

// Make Program class public for integration tests (WebApplicationFactory<Program>)
public partial class Program;
