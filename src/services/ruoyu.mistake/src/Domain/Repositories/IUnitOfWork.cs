using System.Threading;
using System.Threading.Tasks;

namespace Ruoyu.Study.Mistake.Domain.Repositories;

public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken ct = default);
}
