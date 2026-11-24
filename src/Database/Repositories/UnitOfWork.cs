using System;
using System.Linq;
using Emenda.Database.Entities;
using Microsoft.EntityFrameworkCore;
using System.Threading;
using System.Threading.Tasks;
using Emenda.Domain.Repositories;

namespace Emenda.Database.Repositories;

public class UnitOfWork : IUnitOfWork
{
    private readonly MistakeDbContext _dbContext;

    public UnitOfWork(MistakeDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        var practices = _dbContext.ChangeTracker.Entries<MistakePracticeEntity>()
            .Where(entry => entry.State == EntityState.Added && entry.Entity.ImagePaths.Any(path =>
                path.StartsWith("mistakes/extractions/", StringComparison.Ordinal)))
            .Select(entry => entry.Entity).ToArray();
        if (practices.Length == 0) return await _dbContext.SaveChangesAsync(ct);
        await using var transaction = _dbContext.Database.IsRelational() && _dbContext.Database.CurrentTransaction is null
            ? await _dbContext.Database.BeginTransactionAsync(ct) : null;
        await RetainedPracticeImageGuard.ValidateAsync(_dbContext, practices, ct);
        var written = await _dbContext.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return written;
    }
}