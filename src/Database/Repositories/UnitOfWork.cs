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

    public Task<int> SaveChangesAsync(CancellationToken ct = default) => _dbContext.SaveChangesAsync(ct);
}
