using Microsoft.Extensions.Logging;
using Ruoyu.Study.Common.Oss;
using Ruoyu.Study.Mistake.Domain.Models;
using Ruoyu.Study.Mistake.Domain.Services;
using Ruoyu.Study.Mistake.Service.Intake;
using Ruoyu.Study.Mistake.Service.StudentIntegration;

namespace Ruoyu.Study.Mistake.Host.Services.Intake;

/// <summary>
/// Product-owned mistake intake: validates a direct upload, stores the image
/// bytes in OSS, and drives the shared <see cref="LegacyUploadProcessor"/>
/// pipeline under the request-provided (or generated) source key. Idempotent on
/// the source key: an already-processed key returns the existing items without
/// touching OSS.
/// </summary>
public sealed class ProductIntakeService : IProductIntakeService
{
    private static readonly HashSet<string> AllowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg",
        "image/png",
        "image/jpg"
    };

    private readonly IOssService _ossService;
    private readonly IMistakeDomainService _mistakeService;
    private readonly LegacyUploadProcessor _processor;
    private readonly ILogger<ProductIntakeService> _logger;

    public ProductIntakeService(
        IOssService ossService,
        IMistakeDomainService mistakeService,
        LegacyUploadProcessor processor,
        ILogger<ProductIntakeService> logger)
    {
        _ossService = ossService;
        _mistakeService = mistakeService;
        _processor = processor;
        _logger = logger;
    }

    public async Task<ProductIntakeResult> CreateAsync(ProductIntakeRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var invalid = Validate(request);
        if (invalid is not null) return invalid;

        var sourceUploadId = string.IsNullOrWhiteSpace(request.SourceKey)
            ? Guid.NewGuid().ToString("N")
            : request.SourceKey;

        try
        {
            // Idempotency: a repeated source key replays the already-created items
            // and must not store the images again.
            var existingItems = await _mistakeService.GetMistakeItemsByUploadAsync(sourceUploadId).ConfigureAwait(false);
            if (existingItems.Count > 0)
            {
                _logger.LogInformation(
                    "ProductIntake: source {SourceId} was already processed, returning {ItemCount} existing item(s)",
                    sourceUploadId, existingItems.Count);
                return ProductIntakeResult.Completed(sourceUploadId, MapItems(existingItems));
            }

            var uploadedPaths = new List<string>(request.Images.Count);
            foreach (var image in request.Images)
            {
                ct.ThrowIfCancellationRequested();
                var uploadPath = await _ossService.UploadAsync(
                    image.Content,
                    NormalizeFileName(image.FileName),
                    NormalizeContentType(image.ContentType),
                    OssBucket.Uploads).ConfigureAwait(false);
                uploadedPaths.Add(uploadPath);
            }

            var record = new ProcessingUploadRecord
            {
                Id = sourceUploadId,
                StudentId = request.StudentId,
                ImageEntries = uploadedPaths
                    .Select(path => new UploadImageEntry { Path = path, Type = "mistake" })
                    .ToList()
            };

            // The product owns the upload, so there is no Student record to notify.
            var status = await _processor.ProcessAsync(record, notifySource: null, ct).ConfigureAwait(false);
            if (status == LegacyUploadProcessingStatus.VlFailed)
            {
                _logger.LogWarning("ProductIntake: VL analysis failed for source {SourceId}", sourceUploadId);
                return ProductIntakeResult.Failed(
                    ProductIntakeErrorCodes.VlFailed, ProductIntakeMessages.VlFailed);
            }

            var items = await _mistakeService.GetMistakeItemsByUploadAsync(sourceUploadId).ConfigureAwait(false);
            if (items.Count == 0)
            {
                _logger.LogWarning(
                    "ProductIntake: processing source {SourceId} produced no mistake items (status={Status})",
                    sourceUploadId, status);
                return ProductIntakeResult.Failed(
                    ProductIntakeErrorCodes.ProcessingFailed, ProductIntakeMessages.ProcessingFailed);
            }

            _logger.LogInformation(
                "ProductIntake: processed source {SourceId} with {ImageCount} image(s) into {ItemCount} item(s)",
                sourceUploadId, uploadedPaths.Count, items.Count);
            return ProductIntakeResult.Completed(sourceUploadId, MapItems(items));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (SourceIntakeException ex) when (ex.ErrorKind == "reserved_target")
        {
            _logger.LogWarning("ProductIntake: reserved source target path rejected for source {SourceId}",
                sourceUploadId);
            return ProductIntakeResult.Failed(
                ProductIntakeErrorCodes.ReservedTarget, ProductIntakeMessages.ReservedTarget);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ProductIntake: failed to process source {SourceId}", sourceUploadId);
            return ProductIntakeResult.Failed(
                ProductIntakeErrorCodes.ProcessingFailed, ProductIntakeMessages.ProcessingFailed);
        }
    }

    private static ProductIntakeResult? Validate(ProductIntakeRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.StudentId) ||
            request.StudentId.Length > ProductIntakeLimits.MaxStudentIdLength)
        {
            return ProductIntakeResult.Failed(
                ProductIntakeErrorCodes.InvalidRequest, ProductIntakeMessages.StudentIdRequired);
        }

        if (!ProductIntakeLimits.IsValidSourceKey(request.SourceKey))
        {
            return ProductIntakeResult.Failed(
                ProductIntakeErrorCodes.InvalidRequest, ProductIntakeMessages.SourceKeyInvalid);
        }

        if (request.Images is null || request.Images.Count == 0)
        {
            return ProductIntakeResult.Failed(
                ProductIntakeErrorCodes.InvalidRequest, ProductIntakeMessages.ImagesRequired);
        }

        if (request.Images.Count > ProductIntakeLimits.MaxImages)
        {
            return ProductIntakeResult.Failed(
                ProductIntakeErrorCodes.InvalidRequest, ProductIntakeMessages.TooManyImages);
        }

        foreach (var image in request.Images)
        {
            if (image is null ||
                image.Content is null ||
                image.Content.LongLength == 0 ||
                image.Content.LongLength > ProductIntakeLimits.MaxImageBytes)
            {
                return ProductIntakeResult.Failed(
                    ProductIntakeErrorCodes.InvalidRequest, ProductIntakeMessages.ImageSizeInvalid);
            }

            if (!AllowedContentTypes.Contains(NormalizeContentType(image.ContentType)))
            {
                return ProductIntakeResult.Failed(
                    ProductIntakeErrorCodes.InvalidRequest, ProductIntakeMessages.ContentTypeInvalid);
            }
        }

        return null;
    }

    private static List<ProductIntakeItem> MapItems(IEnumerable<MistakeItemModel> items) =>
        items.Select(item => new ProductIntakeItem
        {
            Id = item.Id,
            Subject = item.Subject,
            Grade = item.Grade,
            ReviewStatus = item.ReviewStatus,
            ImagePaths = item.SourceRegions
                .Select(region => region.SourceImagePath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .ToList()
        }).ToList();

    private static string NormalizeContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType)) return string.Empty;
        var separator = contentType.IndexOf(';');
        return (separator >= 0 ? contentType[..separator] : contentType).Trim();
    }

    private static string NormalizeFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return "image";
        var name = Path.GetFileName(fileName);
        return string.IsNullOrWhiteSpace(name) ? "image" : name;
    }
}