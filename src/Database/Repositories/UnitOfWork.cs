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
        return await _dbContext.SaveChangesAsync(ct);
    }
}