using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Emenda.Host.Admin;
using Emenda.Host.Authentication;
using Emenda.Host.Security;
using Emenda.Host.Services.Extraction;
using Emenda.Service.Intake;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Emenda.Host;

public static partial class ExtractionEndpoints
{
    private const long UploadBudget = (long)Rules.MaxSources * Rules.MaxImageBytes + 65536;
    private const int JsonBudget = 2 * 1024 * 1024;

    public static IServiceCollection AddExtraction(this IServiceCollection services, IConfiguration configuration)
    {
        var settings = configuration.GetSection(ExtractionOptions.SectionName).Get<ExtractionOptions>() ?? new();
        if (settings.Enabled && (!settings.IsConfigured || !configuration.GetValue<bool>("Mistake:Layout:Enabled")))
            throw new InvalidOperationException("Enabled Emenda:Extraction requires valid TutorBaseUrl, TutorApiKey, OperationTimeoutSeconds and Mistake:Layout:Enabled.");
        services.AddSingleton<IOptions<ExtractionOptions>>(Options.Create(settings));
        services.AddHttpClient<IQuestionAnalysisClient, AiTutorQuestionClient>(client => client.Timeout = Timeout.InfiniteTimeSpan)
            .RemoveAllLoggers().ConfigurePrimaryHttpMessageHandler(AiTutorQuestionClient.CreateHandler);
        services.AddScoped<ExtractionService>();
        services.AddScoped<IExtractionService>(sp => sp.GetRequiredService<ExtractionService>());
        services.AddScoped<IExtractionDeletionService>(sp => sp.GetRequiredService<ExtractionService>());
        services.AddHostedService<ExtractionHostedService>();
        return services;
    }

    public static WebApplication MapExtractionEndpoints(this WebApplication app)
    {
        MapGroup(app.MapGroup("/api/intake/extractions"), administrator: false);
        var operations = app.MapGroup("/api/ops/extractions");
        MapErrors(operations);
        MapOperations(operations, administrator: false);
        app.MapGet("/admin/extractions", (HttpContext http) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            http.Response.Headers.XContentTypeOptions = "nosniff";
            return Results.Text(ExtractionPage.Render(), ConsoleLayout.MediaType);
        });
        if (app.Services.GetRequiredService<AdminConsoleSettings>().Available)
            MapGroup(app.MapGroup("/admin/api/extractions").RequireAuthorization(AdminConsoleConstants.Policy), administrator: true);
        return app;
    }

    private static void MapErrors(RouteGroupBuilder group)
    {
        group.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            context.HttpContext.Response.Headers.XContentTypeOptions = "nosniff";
            try { return await next(context); }
            catch (ExtractionException error) { return Error(error.Code, error.Status); }
            catch (JsonException) { return Error("invalid_request", 400); }
            catch (BadHttpRequestException error) { return Error("invalid_request", error.StatusCode); }
            catch (InvalidDataException) { return Error("invalid_request", 400); }
            catch (OperationCanceledException) when (context.HttpContext.RequestAborted.IsCancellationRequested) { throw; }
            catch (DbException) { return Error("database_unavailable", 503); }
            catch (IOException) { return Error("storage_unavailable", 503); }
        });
    }

    private static void MapGroup(RouteGroupBuilder group, bool administrator)
    {
        MapErrors(group);
        if (!administrator)
        {
            group.MapPost("", async (HttpContext http, IExtractionService service, CancellationToken ct) =>
            {
                var upload = await UploadAsync(http, ct);
                var result = await service.SubmitAsync(Access(http, false), upload, ct);
                return Results.Json(new { success = true, data = result }, statusCode: StatusCodes.Status202Accepted);
            }).WithMetadata(new RequestSizeLimitAttribute(UploadBudget));
            group.MapPost("/images", async (HttpContext http, IConfiguration configuration, ExtractionService service, CancellationToken ct) =>
            {
                var access = Access(http, false);
                if (!PortalStudentAssertion.TryResolveStudentId(http, configuration, out var studentId))
                    throw new ExtractionException("student_assertion_invalid", 403);
                var form = await ReadUploadFormAsync(http, ct);
                if (form.Keys.Count != 0) throw new ExtractionException("invalid_request");
                var job = await service.SubmitImagesAsync(access, studentId!, await ReadImagesAsync(form, "original", ct), ct);
                return Results.Json(new { success = true, data = ResultSummary.From(job) }, statusCode: StatusCodes.Status202Accepted);
            }).WithMetadata(new RequestSizeLimitAttribute(UploadBudget));
            group.MapGet("/{id:guid}/summary", async (HttpContext http, Guid id, IExtractionService service, CancellationToken ct) =>
                Ok(ResultSummary.From(await service.GetAsync(Access(http, false), id, ct))));
        }
        if (administrator) MapOperations(group, administrator: true);
        group.MapGet("/retained/{itemId:guid}/images/{index:int}",
            async (HttpContext http, Guid itemId, int index, string? size, ExtractionService service, CancellationToken ct) =>
                Image(http, await service.ReadRetainedCropAsync(Access(http, administrator), itemId, index, size, ct)));
        group.MapGet("", async (HttpContext http, string? studentId, int? page, IExtractionService service, CancellationToken ct) =>
            Ok(await service.ListAsync(Access(http, administrator), studentId, page ?? 1, ct)));
        group.MapGet("/{id:guid}", async (HttpContext http, Guid id, IExtractionService service, CancellationToken ct) =>
            Ok(await service.GetAsync(Access(http, administrator), id, ct)));
        group.MapGet("/{id:guid}/versions/{version:int}", async (HttpContext http, Guid id, int version, IExtractionService service, CancellationToken ct) =>
            Ok(await service.GetVersionAsync(Access(http, administrator), id, version, ct)));
        group.MapPost("/{id:guid}/retry", async (HttpContext http, Guid id, IExtractionService service, CancellationToken ct) =>
            Ok(await service.RetryAsync(Access(http, administrator), id, await BodyAsync<ExtractionRetryRequest>(http, ct), ct)));
        group.MapGet("/{id:guid}/sources/{sourceId:guid}", async (HttpContext http, Guid id, Guid sourceId, string? size, IExtractionService service, CancellationToken ct) =>
            Image(http, await service.ReadSourceAsync(Access(http, administrator), id, sourceId, size, ct)));
        group.MapGet("/{id:guid}/questions/{questionId:guid}/images/{version:int}/{index:int}",
            async (HttpContext http, Guid id, Guid questionId, int version, int index, string? size, IExtractionService service, CancellationToken ct) =>
                Image(http, await service.ReadCropAsync(Access(http, administrator), id, questionId, version, index, size, ct)));
        group.MapPost("/{id:guid}/questions/{questionId:guid}/review",
            async (HttpContext http, Guid id, Guid questionId, IExtractionService service,
                Emenda.Database.MistakeDbContext db, Emenda.Domain.Services.IMistakeDomainService mistakes, CancellationToken ct) =>
                await ReviewAsync(http, administrator, id, questionId, service, db, mistakes, ct));
        group.MapPut("/{id:guid}/questions", async (HttpContext http, Guid id, IExtractionService service, CancellationToken ct) =>
            Ok(await service.ReviseAsync(Access(http, administrator), id, await BodyAsync<ExtractionRevisionRequest>(http, ct), ct)));
        group.MapPut("/{id:guid}/questions/{questionId:guid}",
            async (HttpContext http, Guid id, Guid questionId, IExtractionService service, CancellationToken ct) =>
                Ok(await service.ReviseQuestionAsync(Access(http, administrator), id, questionId,
                    await BodyAsync<ExtractionQuestionRevisionRequest>(http, ct), ct)));
        MapCorrectionRoutes(group, administrator);
    }

    private static ExtractionAccess Access(HttpContext http, bool administrator)
    {
        if (administrator)
        {
            if (!http.User.Identities.Any(i => i.IsAuthenticated)) throw new ExtractionException("administrator_required", 403);
            var actor = http.User.FindFirst("sub")?.Value
                ?? http.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                ?? http.User.FindFirst(System.Security.Claims.ClaimTypes.Name)?.Value
                ?? http.User.Identity?.Name;
            if (string.IsNullOrWhiteSpace(actor)) throw new ExtractionException("administrator_identity_missing", 403);
            return new("admin", actor, true);
        }
        var caller = http.Items[ProductCallerAuthenticationMiddleware.CallerNameItemKey]?.ToString();
        if (string.IsNullOrWhiteSpace(caller)) throw new ExtractionException("caller_required", 401);
        return new(caller, caller);
    }

    private static async Task<ExtractionUpload> UploadAsync(HttpContext http, CancellationToken ct)
    {
        var form = await ReadUploadFormAsync(http, ct);
        if (form.Keys.Any(k => k is not ("studentId" or "grade" or "sourceKey")) ||
            form["studentId"].Count != 1 || form["grade"].Count != 1 || form["sourceKey"].Count != 1 ||
            !int.TryParse(form["grade"], NumberStyles.None, CultureInfo.InvariantCulture, out var grade))
            throw new ExtractionException("invalid_request");
        var images = await ReadImagesAsync(form, "original", ct);
        var upload = new ExtractionUpload(form["studentId"].ToString(), grade, form["sourceKey"].ToString(), images);
        Rules.ValidateUpload(upload);
        return upload;
    }

    private static async Task<IFormCollection> ReadUploadFormAsync(HttpContext http, CancellationToken ct)
    {
        if (http.Request.ContentLength > UploadBudget) throw new ExtractionException("request_too_large", 413);
        if (!http.Request.HasFormContentType || http.Request.ContentType?.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase) != true)
            throw new ExtractionException("multipart_required", 415);
        return await ReadMultipartFormAsync(http, ct);
    }

    private static Task<IFormCollection> ReadMultipartFormAsync(HttpContext http, CancellationToken ct)
    {
        var feature = http.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false }) feature.MaxRequestBodySize = UploadBudget;

        var options = new FormOptions
        {
            MultipartBodyLengthLimit = Rules.MaxImageBytes,
            ValueLengthLimit = 4096,
            ValueCountLimit = 3 + Rules.MaxSources,
            MultipartHeadersLengthLimit = 16384
        };
        return new FormFeature(http.Request, options).ReadFormAsync(ct);
    }

    private static async Task<List<ExtractionImage>> ReadImagesAsync(IFormCollection form, string fileName, CancellationToken ct)
    {
        if (form.Files.Count is < 1 or > Rules.MaxSources || form.Files.Any(f => f.Name is not ("images" or "images[]") ||
                f.Length is 0 or > Rules.MaxImageBytes || f.ContentType is not ("image/jpeg" or "image/png")))
            throw new ExtractionException("invalid_request");
        var images = new List<ExtractionImage>();
        foreach (var file in form.Files)
        {
            await using var stream = file.OpenReadStream();
            var content = new byte[checked((int)file.Length)];
            try { await stream.ReadExactlyAsync(content, ct); }
            catch (EndOfStreamException) { throw new ExtractionException("invalid_request"); }
            images.Add(new() { Content = content, ContentType = file.ContentType, FileName = fileName });
        }
        return images;
    }

    private static async Task<T> BodyAsync<T>(HttpContext http, CancellationToken ct)
    {
        if (http.Request.ContentType?.Split(';')[0].Trim() != "application/json") throw new ExtractionException("json_required", 415);
        if (http.Request.ContentLength > JsonBudget) throw new ExtractionException("request_too_large", 413);
        using var body = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var count = await http.Request.Body.ReadAsync(buffer, ct);
            if (count == 0) break;
            if (body.Length + count > JsonBudget) throw new ExtractionException("request_too_large", 413);
            await body.WriteAsync(buffer.AsMemory(0, count), ct);
        }
        return JsonSerializer.Deserialize<T>(body.GetBuffer().AsSpan(0, checked((int)body.Length)), new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new ExtractionException("invalid_request");
    }

    private static IResult Ok(object data) => Results.Json(new { success = true, data });

    // Source ids, versioned crop addresses and retained crops never change their bytes; deleted or retired ones answer 404/410.
    internal static IResult Image(HttpContext http, (byte[] Bytes, string ContentType) image)
    {
        http.Response.Headers.CacheControl = "private, max-age=86400";
        return Results.File(image.Bytes, image.ContentType);
    }
    private static IResult Error(string code, int status) => Results.Json(new { success = false, errorCode = code }, statusCode: status);
}
