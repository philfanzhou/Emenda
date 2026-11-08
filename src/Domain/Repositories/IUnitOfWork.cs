using System.Threading;
using System.Threading.Tasks;

namespace Emenda.Domain.Repositories;

public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken ct = default);
}
