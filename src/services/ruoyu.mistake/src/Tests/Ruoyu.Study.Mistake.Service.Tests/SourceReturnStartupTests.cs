namespace Ruoyu.Study.Mistake.Service.Tests;

public sealed class SourceReturnStartupTests
{
    [Theory]
    [InlineData("invalid_switch", "Mistake:SourceReturns:Enabled must be true or false")]
    [InlineData("student", "Source returns require Student")]
    [InlineData("strict", "Source returns require Student")]
    [InlineData("resource", "Source returns require Student")]
    [InlineData("credentials", "Source returns require RS256 Student service credentials and HTTPS")]
    [InlineData("https", "Source returns require RS256 Student service credentials and HTTPS")]
    public async Task ActualHostRejectsUnsafeActivationBeforeDatabaseOrObjectInitializationAsync(string condition,string diagnostic)
    {
        await using var f=await SourceIntakeFixture.CreateAsync();
        var settings=new Dictionary<string,string?>
        {
            ["Mistake__ConfigMode"]="Local",["ConnectionStrings__MistakeLocal"]=f.Connection,
            ["Mistake__SourceReturns__Enabled"]="true",["Mistake__Student__Enabled"]="true",
            ["IdentityService__MistakeApiStrictAuth"]="true",["IdentityService__MistakeApiResourceAuth"]="true",
            ["IdentityService__Authority"]="https://issuer.invalid",["IdentityService__Issuer"]="https://issuer.invalid",
            ["IdentityService__Audience"]="intake-users",["IdentityService__RequireHttpsMetadata"]="true",
            ["Mistake__Local__Student__Url"]=f.Source.Client!.BaseAddress!.ToString(),
            ["Mistake__Student__ServiceAuthentication__Enabled"]="true",
            ["Mistake__Student__ServiceAuthentication__KeyId"]="fixture",
            ["Mistake__Student__ServiceAuthentication__PrivateKeyPath"]=f.Source.WritePrivateSigningKey()
        };
        switch(condition)
        {
            case "invalid_switch":settings["Mistake__SourceReturns__Enabled"]="maybe";break;
            case "student":settings["Mistake__Student__Enabled"]="false";break;
            case "strict":settings["IdentityService__MistakeApiStrictAuth"]="false";settings["IdentityService__MistakeApiResourceAuth"]="false";break;
            case "resource":settings["IdentityService__MistakeApiResourceAuth"]="false";break;
            case "credentials":settings["Mistake__Student__ServiceAuthentication__Enabled"]="false";break;
            case "https":settings["Mistake__Local__Student__Url"]="http://127.0.0.1:1/";settings["Mistake__Student__ServiceAuthentication__AllowInsecureHttp"]="true";break;
        }
        foreach(var arguments in new[]{Array.Empty<string>(),new[]{"--upgrade-empty-db"}})
        {
            var result=await LocalBootstrapPostgresTests.RunHostAsync(arguments,settings);
            Assert.NotEqual(0,result.ExitCode);Assert.Contains(diagnostic,result.Output);
            Assert.DoesNotContain("Database: PostgreSQL",result.Output);Assert.DoesNotContain("OSS: Internal",result.Output);
            Assert.DoesNotContain("Mistake empty database upgrade verified",result.Output);
        }
        Assert.Empty(f.Reads);Assert.Empty(f.SideEffects);
    }
}
