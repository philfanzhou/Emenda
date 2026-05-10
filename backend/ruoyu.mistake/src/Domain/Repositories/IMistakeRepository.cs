using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Ruoyu.Study.Mistake.Domain.Models;

namespace Ruoyu.Study.Mistake.Domain.Repositories;

public interface IMistakeRepository
{
    Task AddAsync(MistakeModel entity);
    Task<MistakeModel?> GetByIdAsync(Guid id);
    Task<bool> UpdateAsync(MistakeModel model);
    void Remove(MistakeModel entity);
    Task<(List<MistakeModel> Items, int TotalCount)> GetListAsync(string studentId, int subject, int grade, int status, int page, int size);
}