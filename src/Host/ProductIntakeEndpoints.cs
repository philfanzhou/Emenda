using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Emenda.Host.Services.Intake;
using Emenda.Service.Intake;

namespace Emenda.Host;

/// <summary>
/// Product-owned intake HTTP surface: POST /api/intake/uploads accepts a
/// multipart/form-data request (studentId + optional sourceKey + one or more
/// "images"/"images[]" files) and returns the created mistake items. The
/// endpoint is transport-only; validation, OSS storage and processing live in
/// <see cref="IProductIntakeService"/>. Error responses carry a stable code and
/// message, never an internal exception detail.
/// </summary>
public static class ProductIntakeEndpoints
{
    public const string Route = "/api/intake/uploads";
    private const string ImageFieldName = "images";
    private const string ImageArrayFieldName = "images[]";
    private const string DefaultFileName = "image";

    /// <summary>
    /// Registers the product intake service and the reusable legacy upload
    /// processor. The multipart envelope limits are raised to the documented
    /// intake budget (20 images x 15 MiB) so the accepted limits are reachable.
    /// </summary>
    public static IServiceCollection AddProductIntake(this IServiceCollection services)
    {
        services.AddScoped<LegacyUploadProcessor>();
        services.AddScoped<IProductIntakeService, ProductIntakeService>();
        services.Configure<FormOptions>(options =>
        {
            options.MultipartBodyLengthLimit = ProductIntakeLimits.MaxRequestBytes;
            options.BufferBodyLengthLimit = ProductIntakeLimits.MaxRequestBytes;
        });
        return services;
    }

    public static IEndpointRouteBuilder MapProductIntakeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(Route, HandleAsync);
        return endpoints;
    }

    private static async Task<IResult> HandleAsync(
        HttpContext http,
        IProductIntakeService intake,
        CancellationToken ct)
    {
        if (!IsMultipart(http.Request.ContentType))
        {
            return Error(StatusCodes.Status400BadRequest,
                ProductIntakeErrorCodes.InvalidRequest, ProductIntakeMessages.MultipartRequired);
        }

        // The documented intake budget (20 x 15 MiB) is larger than the host's
        // default request-body limit; raise it for this endpoint only.
        var bodySizeFeature = http.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (bodySizeFeature is { IsReadOnly: false })
        {
            bodySizeFeature.MaxRequestBodySize = ProductIntakeLimits.MaxRequestBytes;
        }

        IFormCollection form;
        try
        {
            form = await http.Request.ReadFormAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or BadHttpRequestException)
        {
            return Error(StatusCodes.Status400BadRequest,
                ProductIntakeErrorCodes.InvalidRequest, ProductIntakeMessages.MultipartInvalid);
        }

        var files = form.Files
            .Where(file => string.Equals(file.Name, ImageFieldName, StringComparison.Ordinal) ||
                           string.Equals(file.Name, ImageArrayFieldName, StringComparison.Ordinal))
            .ToList();

        if (files.Count == 0)
        {
            return Error(StatusCodes.Status400BadRequest,
                ProductIntakeErrorCodes.InvalidRequest, ProductIntakeMessages.ImagesRequired);
        }

        if (files.Count > ProductIntakeLimits.MaxImages)
        {
            return Error(StatusCodes.Status400BadRequest,
                ProductIntakeErrorCodes.InvalidRequest, ProductIntakeMessages.TooManyImages);
        }

        if (files.Any(file => file.Length <= 0 || file.Length > ProductIntakeLimits.MaxImageBytes))
        {
            return Error(StatusCodes.Status400BadRequest,
                ProductIntakeErrorCodes.InvalidRequest, ProductIntakeMessages.ImageSizeInvalid);
        }

        var images = new List<ProductIntakeImage>(files.Count);
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            using var content = new MemoryStream();
            await file.CopyToAsync(content, ct).ConfigureAwait(false);
            images.Add(new ProductIntakeImage
            {
                FileName = string.IsNullOrWhiteSpace(file.FileName)
                    ? DefaultFileName
                    : Path.GetFileName(file.FileName),
                ContentType = file.ContentType,
                Content = content.ToArray()
            });
        }

        var result = await intake.CreateAsync(new ProductIntakeRequest
        {
            StudentId = form["studentId"].ToString(),
            SourceKey = string.IsNullOrWhiteSpace(form["sourceKey"].ToString())
                ? null
                : form["sourceKey"].ToString(),
            Images = images
        }, ct).ConfigureAwait(false);

        return result.Success
            ? Results.Json(new
            {
                success = true,
                sourceId = result.SourceId,
                items = result.Items
            }, statusCode: StatusCodes.Status200OK)
            : Error(StatusForErrorCode(result.ErrorCode), result.ErrorCode, result.ErrorMessage);
    }

    private static bool IsMultipart(string? contentType) =>
        contentType is not null &&
        contentType.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase);

    private static int StatusForErrorCode(string? errorCode) => errorCode switch
    {
        ProductIntakeErrorCodes.InvalidRequest => StatusCodes.Status400BadRequest,
        ProductIntakeErrorCodes.ReservedTarget => StatusCodes.Status409Conflict,
        _ => StatusCodes.Status500InternalServerError
    };

    private static IResult Error(int statusCode, string? errorCode, string? message) =>
        Results.Json(new
        {
            success = false,
            errorCode,
            message
        }, statusCode: statusCode);
}