using Microsoft.EntityFrameworkCore;

namespace Ruoyu.Study.Mistake.Database;

internal static class LegacyReturnWriteGuard
{
    internal static async Task EnsureAsync(MistakeDbContext db, Guid itemId, CancellationToken ct = default)
    {
        if (await db.LegacyReturns.AnyAsync(o => o.ItemId == itemId, ct))
            throw new InvalidOperationException("return_in_progress");
    }
}
