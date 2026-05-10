using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Ruoyu.Study.Mistake.Domain.Models;

namespace Ruoyu.Study.Mistake.Domain.Repositories;

public interface IProcessLogRepository
{
    Task AddAsync(ProcessLogModel model);
    Task<List<ProcessLogModel>> GetByMistakeItemIdAsync(Guid mistakeItemId);
    Task RemoveByMistakeItemIdAsync(Guid mistakeItemId);
}
