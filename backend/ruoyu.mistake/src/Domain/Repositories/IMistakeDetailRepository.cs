using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Ruoyu.Study.Mistake.Domain.Models;

namespace Ruoyu.Study.Mistake.Domain.Repositories;

public interface IMistakeDetailRepository
{
    Task AddAsync(MistakeDetailModel model);
    Task<MistakeDetailModel?> GetByMistakeIdAsync(Guid mistakeId);
    Task<bool> UpdateAsync(MistakeDetailModel model);
    Task<bool> DeleteByMistakeIdAsync(Guid mistakeId);
    Task<bool> RemoveImageAsync(Guid mistakeId, string imagePath);
}
