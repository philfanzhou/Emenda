using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Emenda.Domain.Models;

namespace Emenda.Domain.Repositories;

public interface IProcessLogRepository
{
    Task AddAsync(ProcessLogModel model, CancellationToken ct = default);
    Task<List<ProcessLogModel>> GetByMistakeItemIdAsync(Guid mistakeItemId);
    Task RemoveByMistakeItemIdAsync(Guid mistakeItemId);
}
