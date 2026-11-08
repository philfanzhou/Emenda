using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Ruoyu.Study.Mistake.Domain.Models;

namespace Ruoyu.Study.Mistake.Domain.Repositories;

public interface IProcessLogRepository
{
    Task AddAsync(ProcessLogModel model, CancellationToken ct = default);
    Task<List<ProcessLogModel>> GetByMistakeItemIdAsync(Guid mistakeItemId);
    Task RemoveByMistakeItemIdAsync(Guid mistakeItemId);
}
