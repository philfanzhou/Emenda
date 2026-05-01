using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Ruoyu.Study.Mistake.Domain.Models;

namespace Ruoyu.Study.Mistake.Domain.Repositories;

public interface IImageFingerprintRepository
{
    Task AddRangeAsync(List<ImageFingerprintModel> models);
    Task<List<ImageFingerprintModel>> GetByHashesAndStudentIdAsync(List<string> hashes, string studentId);
    Task RemoveByMistakeIdAsync(Guid mistakeId);
    Task RemoveByOssPathAsync(string ossPath);
}
