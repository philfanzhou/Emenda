namespace Ruoyu.Study.Mistake.Service.Intake;

public sealed record SourceHandoffView(string OperationId,string State,Guid? ReceiptId,DateTimeOffset? CompletedAt,string? ErrorKind,bool Sealed=true);
