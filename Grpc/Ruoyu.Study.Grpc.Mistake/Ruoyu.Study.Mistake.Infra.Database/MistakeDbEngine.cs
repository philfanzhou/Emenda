using Qz.Infra.Database.MySql;
using Microsoft.Extensions.Configuration;

namespace Ruoyu.Study.Mistake.Infra.Database;

public class MistakeDbEngine : MySqlDbEngine
{
    public MistakeDbEngine(string connectionString) : base(connectionString)
    {
    }

    public static MistakeDbEngine Create(IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("MistakeDb");
        if (string.IsNullOrEmpty(connectionString))
        {
            throw new InvalidOperationException("MistakeDb connection string is not configured.");
        }
        return new MistakeDbEngine(connectionString);
    }
}
