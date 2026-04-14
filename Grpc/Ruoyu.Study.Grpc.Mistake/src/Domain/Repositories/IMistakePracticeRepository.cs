using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Ruoyu.Study.Mistake.Domain.Models;

namespace Ruoyu.Study.Mistake.Domain.Repositories;

public interface IMistakePracticeRepository
{
    Task AddAsync(MistakePracticeModel entity);
    Task<List<MistakePracticeModel>> GetByMistakeIdAsync(Guid mistakeId);
    Task RemoveByMistakeIdAsync(Guid mistakeId);
}