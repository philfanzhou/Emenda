using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using Serilog;
using Emenda.Common.Oss;
using Emenda.Database;
using Emenda.Database.Repositories;
using Emenda.Domain.Repositories;
using Emenda.Domain.Services;
using Emenda.Host.Services.Layout;
using Emenda.Host.Services.Health;
using Emenda.Host;
using Emenda.Host.Admin;
using Emenda.Host.Authentication;
using Emenda.Host.Security;
using Emenda.Service;

var builder = WebApplication.CreateBuilder(args);

// Emenda is configured from standard appsettings/environment configuration, logs to the
// console, authenticates per-caller product credentials and owns its PostgreSQL database.
// Serilog owns logging, so the framework Logging:LogLevel section is never
// applied: the level below is fixed and logging has no configuration keys.
builder.Host.UseSerilog((_, _, logger) => logger
    .MinimumLevel.Information()
    .Enrich.FromLogContext()
    .WriteTo.Console());

// StructaDoc layout capability switch (IKQA2N). Default off; disabled configuration
// makes zero layout requests. Automatic extraction requires it. The value is fixed
// for this process lifetime.
const string layoutEnabledKey = "Mistake:Layout:Enabled";
var layoutValue = builder.Configuration[layoutEnabledKey];
var layoutEnabled = layoutValue is null ? false : bool.TryParse(layoutValue, out var parsedLayout)
    ? parsedLayout : throw new InvalidOperationException($"{layoutEnabledKey} must be true or false.");
builder.Services.AddSingleton(new LayoutCapabilityOptions(layoutEnabled));
var structaDocLayoutOptions = builder.Configuration.GetSection(StructaDocLayoutOptions.SectionName)
    .Get<StructaDocLayoutOptions>() ?? new StructaDocLayoutOptions();

builder.Services.AddProductCallerAuthentication(builder.Configuration);
// Admin console (SignaCore hosted login + caller management). Registers
// the OIDC slice only when Emenda:Admin:Oidc is fully configured; a missing
// configuration leaves the public API running and the console answers 503.
builder.Services.AddEmendaAdminConsole(builder.Configuration);

// Kestrel has one HTTP/1 listener; URLS and Endpoints:Http are not listener authorities.
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

// Only the explicit product connection is used; the database must be a
// dedicated Emenda database.
var connectionString = builder.Configuration.GetConnectionString("Emenda");
if (string.IsNullOrWhiteSpace(connectionString))
    throw new InvalidOperationException("ConnectionStrings:Emenda is required. Use a dedicated Emenda database.");
var connectionSource = Environment.GetEnvironmentVariable("ConnectionStrings__Emenda") is null ? "File" : "Environment";
Console.WriteLine($"Emenda configuration: DatabaseConnectionSource={connectionSource}.");
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
var ossConfig = new OssOptions();
builder.Configuration.GetSection("Oss").Bind(ossConfig);

builder.Services.AddDbContext<MistakeDbContext>(options =>
{
    options.UseNpgsql(connectionString);
});

builder.Services.AddScoped<IMistakeItemRepository, MistakeItemRepository>();
builder.Services.AddScoped<IMistakePracticeRepository, MistakePracticeRepository>();
builder.Services.AddScoped<IProcessLogRepository, ProcessLogRepository>();
var conversationOptions = builder.Configuration.GetSection("Emenda:ConversationCollection")
    .Get<Emenda.Host.Services.Conversation.ConversationCollectionOptions>() ?? new();
conversationOptions.Validate();
builder.Services.AddSingleton(conversationOptions);
builder.Services.AddHttpClient("ConversationCollection", client => client.Timeout = TimeSpan.FromSeconds(60))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddSingleton<Emenda.Host.Services.Conversation.IConversationAuthorization, Emenda.Host.Services.Conversation.ConversationAuthorizationClient>();
builder.Services.AddScoped<Emenda.Host.Services.Conversation.ConversationCollectionStore>();
builder.Services.AddScoped<Emenda.Host.Services.Conversation.ConversationAnalysisStore>();
builder.Services.AddSingleton<Emenda.Host.Services.Conversation.IConversationExportReader, Emenda.Host.Services.Conversation.ConversationExportClient>();
if (conversationOptions.Enabled) builder.Services.AddHostedService<Emenda.Host.Services.Conversation.ConversationCollectionWorker>();
builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();
builder.Services.AddScoped<IMistakeDomainService, MistakeDomainService>();

// OSS service: the product only reads and writes its own mistakes/ prefix.
builder.Services.AddSingleton<IOssService>(_ => new S3OssService(ossConfig, new[] { "mistakes/" }));

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

// Automatic extraction (StructaDoc layout + paired AI Tutor analysis).
builder.Services.AddExtraction(builder.Configuration);

// Display variants (thumbnails) for every stored mistake image, including the automatic backfill.
var imageVariantOptions = builder.Configuration.GetSection(Emenda.Host.Services.Images.ImageVariantOptions.SectionName)
    .Get<Emenda.Host.Services.Images.ImageVariantOptions>() ?? new();
builder.Services.AddSingleton(imageVariantOptions);
builder.Services.AddScoped<Emenda.Host.Services.Images.ImageVariantBacklog>();
builder.Services.AddHostedService<Emenda.Host.Services.Images.ImageVariantWorker>();

// Readiness dependency matrix (IKJFIX): registered after every capability
// switch and address is resolved so the snapshot reflects the wired process.
builder.Services.AddSingleton(new MistakeReadinessSnapshot(
    layoutEnabled, layoutEnabled ? layoutBaseUrl : string.Empty));
builder.Services.AddHttpClient(MistakeReadinessProbe.HttpClientName)
    .ConfigureHttpClient(client => client.Timeout = MistakeReadinessProbe.ProbeTimeout);
builder.Services.AddScoped<MistakeReadinessProbe>();


var app = builder.Build();

app.Logger.LogInformation("Mistake Service starting");
app.Logger.LogInformation("Endpoint: HTTP={HttpPort}", httpPort);
if (!string.IsNullOrEmpty(connectionString))
{
    var csb = new DbConnectionStringBuilder { ConnectionString = connectionString };
    var pool = new Npgsql.NpgsqlConnectionStringBuilder(connectionString);
    app.Logger.LogInformation("Database pool: {Pool}", StartupDiagnostics.SummarizePostgreSqlPool(
        pool.MaxPoolSize, pool.MinPoolSize, pool.ConnectionIdleLifetime, pool.ConnectionPruningInterval, pool.Timeout, pool.Pooling));
    app.Logger.LogInformation("Database: PostgreSQL {Host}:{Port}/{Database}", csb["Host"], csb.TryGetValue("Port", out var dbPort) ? dbPort : "5432", csb["Database"]);
}
app.Logger.LogInformation(
    "Layout detection: Enabled={Enabled}, BaseUrl={BaseUrl}, ApiKey={ApiKeyStatus}",
    layoutEnabled,
    layoutEnabled ? layoutBaseUrl : StructaDocLayoutClient.DisabledPlaceholderBaseUrl,
    layoutEnabled ? StartupDiagnostics.MaskApiKey(structaDocLayoutOptions.ApiKey) : "(capability disabled)");
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

{
    var callerOptions = app.Services
        .GetRequiredService<IOptions<ProductCallerAuthenticationOptions>>().Value;
    app.Logger.LogInformation(
        "Product caller authentication: AllowAnonymous={AllowAnonymous}, Callers={CallerCount}",
        callerOptions.AllowAnonymous, callerOptions.ValidCallers.Count);
    var adminSettings = app.Services.GetRequiredService<AdminConsoleSettings>();
    app.Logger.LogInformation(
        "Admin console: Available={Available}, Authority={Authority}, MissingKeys={MissingKeys}",
        adminSettings.Available, adminSettings.Available ? adminSettings.Authority : "(not configured)",
        adminSettings.MissingKeys.Count == 0 ? "none" : string.Join(", ", adminSettings.MissingKeys));
}

app.Logger.LogInformation("OSS: Endpoint={Endpoint}, Bucket={Bucket}, Prefixes=mistakes/",
    ossConfig.InternalEndpoint ?? "N/A", ossConfig.BucketName ?? "N/A");

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

// Product callers present a per-caller credential; health, contract and
// root endpoints stay anonymous.
app.UseMiddleware<ProductCallerAuthenticationMiddleware>();
// Admin console session boundary (fixed 401/403 before the console API).
app.UseMiddleware<AdminConsoleSessionMiddleware>();
app.Use(async (http, next) =>
{
    if (http.Request.Path.StartsWithSegments("/api"))
        http.RequestServices.GetRequiredService<MistakeDbContext>().ConversationCallerScope =
            http.Items[ProductCallerAuthenticationMiddleware.CallerNameItemKey] as string ?? "";
    try { await next(http); }
    catch (DbUpdateException error) when (!http.Response.HasStarted && error.InnerException is PostgresException
        { SqlState: PostgresErrorCodes.CheckViolation, MessageText: "extraction_object_retired" })
    {
        http.Response.StatusCode = StatusCodes.Status409Conflict;
        await http.Response.WriteAsJsonAsync(new { success = false, errorCode = "extraction_object_retired",
            message = "题图已退役，请重新读取当前错题。" }, http.RequestAborted);
    }
});
app.MapMistakeHttpEndpoints();
// Admin console page, hosted-login protocol endpoints and the caller management API.
app.MapAdminConsole();
app.MapExtractionEndpoints();
app.MapMistakeImageEndpoints();
Emenda.Host.Services.Conversation.ConversationCollectionEndpoints.MapConversationCollectionEndpoints(app);
app.MapMistakeContractEndpoints();
// Health endpoints (IKJFIX): anonymous, outside the /api group.
app.MapMistakeHealthEndpoints();
app.MapGet("/", () => "Mistake WebAPI host is running.");


app.Run();

// Make Program class public for integration tests (WebApplicationFactory<Program>)
public partial class Program;
