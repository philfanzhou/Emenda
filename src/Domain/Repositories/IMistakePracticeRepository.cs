using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Emenda.Domain.Models;

namespace Emenda.Domain.Repositories;

public interface IMistakePracticeRepository
{
    Task AddAsync(MistakePracticeModel model);
    Task<List<MistakePracticeModel>> GetByMistakeItemIdAsync(Guid mistakeItemId);
    Task RemoveByMistakeItemIdAsync(Guid mistakeItemId);
    Task<List<MistakePracticeModel>> GetAllAsync();
}
