namespace Emenda.Host.Services.Extraction;

public sealed record ExtractionJobSummary(Guid Id, string Caller, string StudentId, DateTimeOffset CreatedAt,
    string Status, int MistakeCount, int ConfirmedCount);

public sealed record ExtractionSummaryPage(IReadOnlyList<ExtractionJobSummary> Items, int? NextPage);
