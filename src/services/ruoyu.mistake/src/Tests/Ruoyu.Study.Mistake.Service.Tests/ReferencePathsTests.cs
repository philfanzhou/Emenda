using System.Text.Json;
using Ruoyu.Study.Mistake.Host.Services.Intake;
using Xunit;

namespace Ruoyu.Study.Mistake.Service.Tests;

public sealed class ReferencePathsTests
{
    [Theory]
    [InlineData("paths", "[null]")]
    [InlineData("paths", "[\"\"]")]
    [InlineData("regions", "[{\"sourceImagePath\":\"\"}]")]
    [InlineData("manifest", "{\"images\":[{}]}")]
    [InlineData("targets", "[{\"sourcePath\":\"uploads/a\"}]")]
    [InlineData("original", "{}")]
    [InlineData("unknown", "[\"uploads/a\"]")]
    public void RejectsIncompleteReferences(string format, string data)
    {
        Assert.ThrowsAny<Exception>(() => ReferencePaths.Parse(format, data));
    }

    [Fact]
    public void ReadsOnlyTheDeclaredFields()
    {
        Assert.Empty(ReferencePaths.Parse("paths", "[]"));
        Assert.Equal(["uploads/case A.jpg"], ReferencePaths.Parse("manifest", """
            {"images":[{"path":"uploads/case A.jpg","type":"homework"}],"comments":"uploads/guess.jpg"}
            """));
        Assert.Equal(["uploads/a", "mistakes/b"], ReferencePaths.Parse("targets", """
            [{"sourcePath":"uploads/a","targetKey":"mistakes/b"}]
            """));
        Assert.Equal(["uploads/r"], ReferencePaths.Parse("returned", """{"imageEntries":[{"path":"uploads/r"}]}"""));
        Assert.Equal(["uploads/o"], ReferencePaths.Parse("original", """{"sourceRegions":[{"sourceImagePath":"uploads/o"}]}"""));
        Assert.Throws<JsonException>(() => ReferencePaths.Parse("paths", "{\"message\":\"uploads/guess.jpg\"}"));
    }
}
