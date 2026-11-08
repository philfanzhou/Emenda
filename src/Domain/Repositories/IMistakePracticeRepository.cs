using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Ruoyu.Study.Mistake.Domain.Models;

namespace Ruoyu.Study.Mistake.Domain.Repositories;

public interface IMistakePracticeRepository
{
    Task AddAsync(MistakePracticeModel model);
    Task<List<MistakePracticeModel>> GetByMistakeItemIdAsync(Guid mistakeItemId);
    Task RemoveByMistakeItemIdAsync(Guid mistakeItemId);
    Task<List<MistakePracticeModel>> GetAllAsync();
}
