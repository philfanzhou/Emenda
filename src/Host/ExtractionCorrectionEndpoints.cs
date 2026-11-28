using System.Globalization;
using Emenda.Host.Services.Extraction;
using Emenda.Service.Intake;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;

namespace Emenda.Host;

public static partial class ExtractionEndpoints
{
    private static void MapCorrectionRoutes(RouteGroupBuilder group, bool administrator)
    {
        group.MapPost("/{id:guid}/proposals", async (HttpContext http, Guid id, IExtractionService service, CancellationToken ct) =>
            Ok(await service.GenerateProposalAsync(Access(http, administrator), id, await BodyAsync<ExtractionProposalRequest>(http, ct), ct)));
        group.MapGet("/{id:guid}/proposals", async (HttpContext http, Guid id, IExtractionService service, CancellationToken ct) =>
            Ok(await service.ListProposalsAsync(Access(http, administrator), id, ct)));
        group.MapGet("/{id:guid}/proposals/{proposalId:guid}",
            async (HttpContext http, Guid id, Guid proposalId, IExtractionService service, CancellationToken ct) =>
                Ok(await service.GetProposalAsync(Access(http, administrator), id, proposalId, ct)));
        group.MapPost("/{id:guid}/proposals/{proposalId:guid}/discard",
            async (HttpContext http, Guid id, Guid proposalId, IExtractionService service, CancellationToken ct) =>
                Ok(await service.DiscardProposalAsync(Access(http, administrator), id, proposalId, ct)));
        group.MapPost("/{id:guid}/proposals/{proposalId:guid}/adopt",
            async (HttpContext http, Guid id, Guid proposalId, IExtractionService service, CancellationToken ct) =>
                Ok(await service.AdoptProposalAsync(Access(http, administrator), id, proposalId,
                    await BodyAsync<ExtractionProposalAdoptionRequest>(http, ct), ct)));
        group.MapPost("/{id:guid}/questions/{questionId:guid}/adjust",
            async (HttpContext http, Guid id, Guid questionId, IExtractionService service, CancellationToken ct) =>
                Ok(await service.AdjustQuestionAsync(Access(http, administrator), id, questionId,
                    await BodyAsync<ExtractionAdjustmentRequest>(http, ct), ct)));
        group.MapPost("/{id:guid}/questions/{questionId:guid}/remove-mistake",
            async (HttpContext http, Guid id, Guid questionId, IExtractionService service, CancellationToken ct) =>
                Ok(await service.RemoveMistakeAsync(Access(http, administrator), id, questionId,
                    await BodyAsync<ExtractionMistakeRemovalRequest>(http, ct), ct)));
        group.MapPost("/{id:guid}/questions/{questionId:guid}/supplements",
            async (HttpContext http, Guid id, Guid questionId, IExtractionService service, CancellationToken ct) =>
                Ok(await service.RequestSupplementAsync(Access(http, administrator), id, questionId,
                    await BodyAsync<ExtractionSupplementRequest>(http, ct), ct)));
        group.MapGet("/{id:guid}/questions/{questionId:guid}/supplements/{supplementId:guid}",
            async (HttpContext http, Guid id, Guid questionId, Guid supplementId, IExtractionService service, CancellationToken ct) =>
                Ok(await service.GetSupplementAsync(Access(http, administrator), id, questionId, supplementId, ct)));
        group.MapPost("/{id:guid}/questions/{questionId:guid}/supplements/{supplementId:guid}/images",
            async (HttpContext http, Guid id, Guid questionId, Guid supplementId, IExtractionService service, CancellationToken ct) =>
                Ok(await service.AppendSupplementAsync(Access(http, administrator), id, questionId, supplementId,
                    await SupplementUploadAsync(http, ct), ct)))
            .WithMetadata(new RequestSizeLimitAttribute(UploadBudget));
    }

    private static async Task<ExtractionSupplementUpload> SupplementUploadAsync(HttpContext http, CancellationToken ct)
    {
        if (http.Request.ContentLength > UploadBudget) throw new ExtractionException("request_too_large", 413);
        if (http.Request.ContentType?.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase) != true)
            throw new ExtractionException("multipart_required", 415);
        var feature = http.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false }) feature.MaxRequestBodySize = UploadBudget;
        var form = await new FormFeature(http.Request, new FormOptions { MultipartBodyLengthLimit = ExtractionRules.MaxImageBytes,
            ValueLengthLimit = 4096, ValueCountLimit = 3 + ExtractionRules.MaxSources }).ReadFormAsync(ct);
        if (form.Keys.Any(k => k is not ("requestKey" or "expectedVersion" or "itemId"))
            || form["requestKey"].Count != 1 || form["expectedVersion"].Count != 1 || form["itemId"].Count != 1
            || !Guid.TryParse(form["requestKey"], out var key) || !Guid.TryParse(form["itemId"], out var item)
            || !int.TryParse(form["expectedVersion"], NumberStyles.None, CultureInfo.InvariantCulture, out var version)
            || form.Files.Count is < 1 or > ExtractionRules.MaxSources || form.Files.Any(f => f.Name is not ("images" or "images[]")
                || f.Length is 0 or > ExtractionRules.MaxImageBytes || f.ContentType is not ("image/jpeg" or "image/png")))
            throw new ExtractionException("invalid_request");
        var images = new List<ExtractionImage>();
        foreach (var file in form.Files)
        {
            await using var stream = file.OpenReadStream();
            var bytes = new byte[checked((int)file.Length)];
            try { await stream.ReadExactlyAsync(bytes, ct); }
            catch (EndOfStreamException) { throw new ExtractionException("invalid_request"); }
            images.Add(new() { Content = bytes, ContentType = file.ContentType, FileName = "supplement" });
        }
        return new(key, version, item, images);
    }
}
