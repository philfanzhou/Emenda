using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Emenda.Common.Oss;
using Emenda.Database;
using Emenda.Database.Entities;
using Emenda.Domain.Models;
using Emenda.Domain.Services;
using Emenda.Host.Services.VlImageAnalysis;
using Emenda.Service.Intake;
using Emenda.Service.Services;

namespace Emenda.Host.Services.Intake;

/// <summary>
/// Binds a caller's request before object writes. Deterministic, product-owned
/// objects survive retries; all items and the immutable completion receipt commit
/// together. PostgreSQL transaction locks serialize writers across Host instances.
/// </summary>
public sealed class ProductIntakeService(
    IOssService oss,
    IMistakeDomainService mistakes,
    MistakeDbContext db,
    IVlAnalyzer analyzer,
    IOptions<VlAnalyzerOptions> vlOptions,
    ILogger<ProductIntakeService> logger) : IProductIntakeService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<ProductIntakeResult> CreateAsync(ProductIntakeRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var invalid = Validate(request);
        if (invalid is not null) return invalid;

        var requestId = ProductIntakeIdentity.RequestId(request.CallerName, request.SourceKey ?? Guid.NewGuid().ToString("N"));
        var payloadHash = ProductIntakeIdentity.PayloadHash(request);
        var lockKey = ProductIntakeIdentity.LockKey(requestId);
        try
        {
            ProductIntakeRequestEntity receipt;
            // This short transaction makes the payload binding survive even if
            // object storage, analysis, or the later item transaction fails.
            await using (var binding = await db.Database.BeginTransactionAsync(ct))
            {
                if (!await db.Database.SqlQuery<bool>(
                    $"SELECT pg_try_advisory_xact_lock({lockKey}) AS \"Value\"").SingleAsync(ct))
                    return ProductIntakeResult.Failed(ProductIntakeErrorCodes.InProgress, ProductIntakeMessages.InProgress);
                receipt = await db.ProductIntakeRequests.FindAsync([requestId], ct)
                    ?? new ProductIntakeRequestEntity
                    {
                        RequestId = requestId,
                        Caller = request.CallerName,
                        StudentId = request.StudentId,
                        PayloadHash = payloadHash,
                        CreatedAt = DateTimeOffset.UtcNow
                    };
                if (!string.Equals(receipt.PayloadHash, payloadHash, StringComparison.Ordinal))
                    return ProductIntakeResult.Failed(ProductIntakeErrorCodes.Conflict, ProductIntakeMessages.Conflict);
                if (db.Entry(receipt).State == EntityState.Detached)
                {
                    db.ProductIntakeRequests.Add(receipt);
                    await db.SaveChangesAsync(ct);
                }
                await binding.CommitAsync(ct);
            }

            if (receipt.Response is not null) return Replay(receipt.Response);

            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var acquired = await db.Database.SqlQuery<bool>(
                $"SELECT pg_try_advisory_xact_lock({lockKey}) AS \"Value\"").SingleAsync(ct);
            if (!acquired)
                return ProductIntakeResult.Failed(ProductIntakeErrorCodes.InProgress, ProductIntakeMessages.InProgress);
            await db.Entry(receipt).ReloadAsync(ct);
            if (receipt.Response is not null) return Replay(receipt.Response);

            var paths = await StoreImagesAsync(request, requestId, ct);
            List<VlImageGroup> groups;
            try
            {
                groups = vlOptions.Value.IsConfigured
                    ? (await analyzer.AnalyzeVerifiedAsync(request.StudentId, paths,
                        request.Images.Select(image => image.Content).ToArray(), ct)).Groups
                    : [new VlImageGroup
                    {
                        ImageIndices = Enumerable.Range(0, paths.Count).ToList(),
                        Subject = 1,
                        Grade = 7,
                        RootCause = "Mistake upload (VL skipped)"
                    }];
                ValidateGroups(groups, paths.Count);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                logger.LogWarning(error, "Product intake analysis failed for {RequestId}", requestId);
                return ProductIntakeResult.Failed(ProductIntakeErrorCodes.VlFailed, ProductIntakeMessages.VlFailed);
            }

            var created = new List<MistakeItemModel>(groups.Count);
            foreach (var group in groups)
            {
                ct.ThrowIfCancellationRequested();
                created.AddRange(await mistakes.SubmitMistakeUploadAsync(
                    request.StudentId, group.Subject, group.Grade,
                    group.ImageIndices.Select(index => paths[index]).ToList(), group.RootCause, requestId));
            }
            var result = ProductIntakeResult.Completed(requestId, created.Select(item => new ProductIntakeItem
            {
                Id = item.Id,
                Subject = item.Subject,
                Grade = item.Grade,
                ReviewStatus = item.ReviewStatus,
                ImagePaths = item.SourceRegions.Select(region => region.SourceImagePath).ToList()
            }).ToList());
            receipt.Response = JsonSerializer.Serialize(result, Json);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            db.ChangeTracker.Clear();
            throw;
        }
        catch (Exception error)
        {
            db.ChangeTracker.Clear();
            // A failed/unknown COMMIT never becomes success. Retrying the bound
            // request either finds the committed receipt or reruns the rolled-back batch.
            logger.LogError(error, "Product intake failed for {RequestId}", requestId);
            return ProductIntakeResult.Failed(ProductIntakeErrorCodes.ProcessingFailed, ProductIntakeMessages.ProcessingFailed);
        }
    }

    private async Task<List<string>> StoreImagesAsync(ProductIntakeRequest request, string requestId, CancellationToken ct)
    {
        var paths = new List<string>(request.Images.Count);
        var folder = $"intake/{requestId}";
        for (var index = 0; index < request.Images.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var image = request.Images[index];
            var name = ProductIntakeIdentity.FileName(index, image.ContentType);
            var path = $"mistakes/{folder}/{name}";
            if (!await oss.ObjectExistsAsync(path))
            {
                var stored = await oss.UploadAsync(image.Content, name,
                    ProductIntakeIdentity.ContentType(image.ContentType), OssBucket.Mistakes, folder);
                if (!string.Equals(stored, path, StringComparison.Ordinal))
                    throw new InvalidOperationException("Object storage returned an unexpected product object key.");
            }
            // A lost PUT response is retryable at the same key. Never trust an
            // existence check alone, or overwrite bytes that contradict the binding.
            await using var stream = await oss.DownloadAsync(path);
            var digest = await SHA256.HashDataAsync(stream, ct);
            if (!CryptographicOperations.FixedTimeEquals(digest, SHA256.HashData(image.Content)))
                throw new InvalidOperationException("Product image content verification failed.");
            paths.Add(path);
        }
        return paths;
    }

    private static ProductIntakeResult Replay(string json) =>
        JsonSerializer.Deserialize<ProductIntakeResult>(json, Json)
        ?? throw new InvalidOperationException("Invalid product intake receipt.");

    private static void ValidateGroups(List<VlImageGroup> groups, int imageCount)
    {
        var used = new HashSet<int>();
        if (groups.Count == 0) throw new InvalidOperationException("Empty product intake analysis.");
        foreach (var group in groups)
        {
            if (group.Subject is < 1 or > 9 || group.Grade is < 1 or > 12 || group.ImageIndices.Count == 0)
                throw new InvalidOperationException("Invalid product intake analysis group.");
            foreach (var index in group.ImageIndices)
                if (index < 0 || index >= imageCount || !used.Add(index))
                    throw new InvalidOperationException("Invalid or overlapping product intake image grouping.");
        }
        if (used.Count != imageCount) throw new InvalidOperationException("Incomplete product intake image grouping.");
    }

    private static ProductIntakeResult? Validate(ProductIntakeRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.CallerName))
            return ProductIntakeResult.Failed(ProductIntakeErrorCodes.InvalidRequest, "A trusted caller is required.");
        if (string.IsNullOrWhiteSpace(request.StudentId) || request.StudentId.Length > ProductIntakeLimits.MaxStudentIdLength)
            return ProductIntakeResult.Failed(ProductIntakeErrorCodes.InvalidRequest, ProductIntakeMessages.StudentIdRequired);
        if (!ProductIntakeLimits.IsValidSourceKey(request.SourceKey))
            return ProductIntakeResult.Failed(ProductIntakeErrorCodes.InvalidRequest, ProductIntakeMessages.SourceKeyInvalid);
        if (request.Images is null || request.Images.Count == 0)
            return ProductIntakeResult.Failed(ProductIntakeErrorCodes.InvalidRequest, ProductIntakeMessages.ImagesRequired);
        if (request.Images.Count > ProductIntakeLimits.MaxImages)
            return ProductIntakeResult.Failed(ProductIntakeErrorCodes.InvalidRequest, ProductIntakeMessages.TooManyImages);
        foreach (var image in request.Images)
        {
            if (image?.Content is null || image.Content.Length == 0 || image.Content.LongLength > ProductIntakeLimits.MaxImageBytes)
                return ProductIntakeResult.Failed(ProductIntakeErrorCodes.InvalidRequest, ProductIntakeMessages.ImageSizeInvalid);
            if (ProductIntakeIdentity.ContentType(image.ContentType) is not ("image/jpeg" or "image/png"))
                return ProductIntakeResult.Failed(ProductIntakeErrorCodes.InvalidRequest, ProductIntakeMessages.ContentTypeInvalid);
        }
        return null;
    }
}
