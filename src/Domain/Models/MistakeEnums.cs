namespace Ruoyu.Study.Mistake.Domain.Models;

/// <summary>
/// Mistake item review status. Migrated from proto ReviewStatus enum.
/// Values are stored as int in mistake_item.review_status column.
/// </summary>
public enum ReviewStatus
{
    Unspecified = 0,
    PendingReview = 1,
    Confirmed = 2,
    Rejected = 3,
}

/// <summary>
/// Mistake process action types. Migrated from proto ProcessAction enum.
/// Values are stored as int in process_log.action column.
/// </summary>
public enum ProcessAction
{
    Unspecified = 0,
    Created = 1,
    Analyzed = 2,
    ReviewConfirmed = 3,
    ReviewRejected = 4,
    Practiced = 5,
    Mastered = 6,
    Reanalyzed = 7,
}
