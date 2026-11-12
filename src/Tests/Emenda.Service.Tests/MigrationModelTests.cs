using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Emenda.Database;

namespace Emenda.Service.Tests;

public sealed class MigrationModelTests
{
    [Fact]
    public void PublishedMigrationsCoverTheRuntimeDatabaseModel()
    {
        using var db = new MistakeDbContext(new DbContextOptionsBuilder<MistakeDbContext>()
            .UseNpgsql("Host=127.0.0.1;Database=metadata_only;Username=metadata_only")
            .Options);
        db.Database.HasPendingModelChanges().Should().BeFalse(
            "an Own-mode empty database must be creatable without suppressing EF's schema-change checks");
    }
}
