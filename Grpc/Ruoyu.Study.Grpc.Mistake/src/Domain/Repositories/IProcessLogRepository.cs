using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Ruoyu.Study.Mistake.Domain.Models;

namespace Ruoyu.Study.Mistake.Domain.Repositories;

public interface IProcessLogRepository
{
    Task AddAsync(ProcessLogModel entity);
    Task<List<ProcessLogModel>> GetByMistakeIdAsync(Guid mistakeId);
    Task RemoveByMistakeIdAsync(Guid mistakeId);
}