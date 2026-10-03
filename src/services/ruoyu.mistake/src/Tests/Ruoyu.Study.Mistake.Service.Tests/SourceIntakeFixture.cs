using System.Text.Json;
using System.Data.Common;
using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Moq;
using Npgsql;
using Ruoyu.Study.Common.Authentication;
using Ruoyu.Study.Common.Oss;
using Ruoyu.Study.Mistake.Database;
using Ruoyu.Study.Mistake.Database.Repositories;
using Ruoyu.Study.Mistake.Domain.Models;
using Ruoyu.Study.Mistake.Domain.Repositories;
using Ruoyu.Study.Mistake.Domain.Services;
using Ruoyu.Study.Mistake.Host;
using Ruoyu.Study.Mistake.Host.Services;
using Ruoyu.Study.Mistake.Host.Services.Intake;
using Ruoyu.Study.Mistake.Host.Services.VlImageAnalysis;
using Ruoyu.Study.Mistake.Service.Intake;
using Ruoyu.Study.Mistake.Service.Services;
using Ruoyu.Study.Mistake.Service.StudentIntegration;
using Ruoyu.Study.Mistake.StudentClient;
using StudentSourceFixture=Ruoyu.Study.Student.Domain.Tests.SourceSnapshotFixture;
using StudentRecord=Ruoyu.Study.Student.Domain.Models.ImageUploadRecordModel;
namespace Ruoyu.Study.Mistake.Service.Tests;

internal sealed class SourceIntakeFixture : IAsyncDisposable
{
    private readonly string _admin;
    internal string Name { get; }
    internal string Connection { get; }
    internal StudentSourceFixture Source { get; }
    internal Guid Student { get; }=Guid.NewGuid();
    internal Guid Actor { get; }=Guid.NewGuid();
    internal RSA UserKey { get; }=RSA.Create(2048);
    internal Mock<IVlAnalyzer> Analyzer { get; }=new();
    internal Mock<IOssService> Oss { get; }=new();
    internal List<string> SideEffects { get; }=[];
    internal List<string> Reads { get; }=[];
    internal CommitFault Fault { get; }=new();
    internal Func<string,Task>? BeforeRead { get; set; }
    internal bool ConfiguredVl { get; set; }
    internal bool Enabled { get; set; }=true;
    internal bool TargetsEnabled { get; set; }
    internal bool HandoffsEnabled { get; set; }
    internal bool ReturnsEnabled { get; set; }
    internal Func<string,string,Task>? Copy { get; set; }
    internal WebApplication? App { get; private set; }
    internal HttpClient? Client { get; private set; }
    internal HttpClient SnapshotHttp { get; }
    internal StudentServiceAuthenticationOptions Identity { get; }
    internal SourceSnapshotClient Snapshots { get; }
    internal SourceIntakeFixture(string admin,string name,StudentSourceFixture source)
    {
        _admin=admin;Name=name;Connection=new NpgsqlConnectionStringBuilder(admin){Database=name}.ConnectionString;Source=source;
        var configuration=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
        {
            [StudentServiceAuthenticationOptions.Section+":Enabled"]="true",
            [StudentServiceAuthenticationOptions.Section+":KeyId"]="fixture",
            [StudentServiceAuthenticationOptions.Section+":PrivateKeyPath"]=source.WritePrivateSigningKey()
        }).Build();
        Identity=StudentServiceAuthenticationOptions.Read(configuration,source.Client!.BaseAddress!.ToString());
        var token=new StudentServiceTokenHandler(Identity){InnerHandler=source.CreateTrustedHandler()};
        SnapshotHttp=new HttpClient(token){BaseAddress=source.Client.BaseAddress};
        Snapshots=new SourceSnapshotClient(SnapshotHttp,Identity);
        Oss.Setup(o=>o.DownloadAsync(It.IsAny<string>())).Returns(async (string path)=>
        { lock(Reads) Reads.Add(path); if(BeforeRead!=null) await BeforeRead(path); return await source.Files.DownloadAsync(path); });
        Oss.Setup(o=>o.ObjectExistsAsync(It.IsAny<string>())).Returns((string path)=>source.Files.ObjectExistsAsync(path));
        Oss.Setup(o=>o.GetPresignedUrlAsync(It.IsAny<string>(),It.IsAny<int>())).Returns((string path,int expiry)=>source.Files.GetPresignedUrlAsync(path,expiry));
        Oss.Setup(o=>o.CopyObjectAsync(It.IsAny<string>(),It.IsAny<string>())).Returns((string a,string b)=>
        {SideEffects.Add("copy");return Copy==null ? Task.CompletedTask : Copy(a,b);});
        Oss.Setup(o=>o.DeleteAsync(It.IsAny<string>())).Returns((string path)=>{SideEffects.Add("delete");return Task.FromResult(true);});
    }
    internal static async Task<SourceIntakeFixture> CreateAsync(bool initialize=true,bool sourceStrict=false)
    {
        var admin=Environment.GetEnvironmentVariable("MISTAKE_INTAKE_TEST_CONNECTION");
        if(string.IsNullOrWhiteSpace(admin)) throw Xunit.Sdk.SkipException.ForSkip("Set MISTAKE_INTAKE_TEST_CONNECTION to owned PostgreSQL 15.");
        var source=await StudentSourceFixture.CreateAsync();await source.StartAsync(strict:sourceStrict);
        var name="mistake_intake_"+Guid.NewGuid().ToString("N");
        await using(var connection=new NpgsqlConnection(admin))
        {await connection.OpenAsync();await using var command=new NpgsqlCommand($"CREATE DATABASE \"{name}\"",connection);await command.ExecuteNonQueryAsync();}
        var fixture=new SourceIntakeFixture(admin,name,source);
        try {if(initialize){await using var db=fixture.Context();await DatabaseInitializer.UpgradeEmptyAsync(db);}}
        catch {await fixture.DisposeAsync();throw;}
        return fixture;
    }
    internal DbContextOptions<MistakeDbContext> DatabaseOptions=>new DbContextOptionsBuilder<MistakeDbContext>().UseNpgsql(Connection).AddInterceptors(Fault).Options;
    internal MistakeDbContext Context()=>new(DatabaseOptions);
    internal SourceIntakeService Service(SourceReturnClient? returnClient=null)=>new(DatabaseOptions,new SourceIntakeOptions(Enabled),Snapshots,Oss.Object,Analyzer.Object,Options.Create(new VlAnalyzerOptions {ApiKey=ConfiguredVl ? "fixture" : ""}),new SourceTargetOptions(TargetsEnabled),new SourceReturnOptions(ReturnsEnabled),returnClient ?? new SourceReturnClient(SnapshotHttp,Identity),new SourceHandoffOptions(HandoffsEnabled),new SourceHandoffClient(SnapshotHttp,Identity));
    internal async Task<StudentRecord> SeedAsync(string[]? paths=null,string[]? types=null,int[]? rotations=null)
    {
        var record=await Source.SeedAsync(paths,types:types,rotations:rotations);
        await using var context=Source.Context();
        var entity=await context.ImageUploadRecords.SingleAsync(r=>r.Id==record.Id);entity.StudentId=Student.ToString();await context.SaveChangesAsync();
        record.StudentId=entity.StudentId;record.ContentRevision=entity.ContentRevision;
        await Source.SqlAsync("INSERT INTO student_profile(id,name,grade,created_at,updated_at) VALUES (@id,'Fixture',7,now(),now()) ON CONFLICT DO NOTHING",new NpgsqlParameter("id",Student));
        await Source.SqlAsync("INSERT INTO student_user_mapping(student_id,user_id,is_default) VALUES (@id,@actor,true) ON CONFLICT DO NOTHING",new NpgsqlParameter("id",Student),new NpgsqlParameter("actor",Actor.ToString()));
        return record;
    }
    internal List<SourceRegionModel> Regions(StudentRecord record)=>record.ImagePaths.Where(i=>i.Type=="mistake").Select(i=>new SourceRegionModel {SourceImagePath=i.Path}).ToList();
    internal Task<List<MistakeItemModel>> ManualAsync(StudentRecord record,Guid? key=null,List<SourceRegionModel>? regions=null,int subject=1,string? cause=null)=>
        Service().CreateManualAsync(record.Id,record.ContentRevision,key ?? Guid.NewGuid(),Actor.ToString(),record.StudentId,subject,7,cause,regions ?? Regions(record));
    internal async Task SqlAsync(string sql,params NpgsqlParameter[] parameters)
    {await using var connection=new NpgsqlConnection(Connection);await connection.OpenAsync();await using var command=new NpgsqlCommand(sql,connection);command.Parameters.AddRange(parameters);await command.ExecuteNonQueryAsync();}
    internal async Task<T> ScalarAsync<T>(string sql)
    {await using var connection=new NpgsqlConnection(Connection);await connection.OpenAsync();await using var command=new NpgsqlCommand(sql,connection);return (T)(await command.ExecuteScalarAsync() ?? throw new InvalidOperationException());}
    internal string Token(string role="admin",string? issuer=null,string? audience=null,string? sub=null,bool expired=false)
    {
        var now=DateTime.UtcNow;
        var token=new JwtSecurityToken(issuer ?? "https://intake-issuer.invalid",audience ?? "intake-users",
            [new Claim("sub",sub ?? Actor.ToString()),new Claim("role",role)],expired ? now.AddMinutes(-3) : now.AddSeconds(-1),expired ? now.AddMinutes(-1) : now.AddMinutes(1),
            new SigningCredentials(new RsaSecurityKey(UserKey),SecurityAlgorithms.RsaSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
    internal async Task StartAsync(bool strict=true,bool resource=true,bool handoffWorker=false)
    {
        Client?.Dispose();if(App!=null)await App.DisposeAsync();
        var builder=WebApplication.CreateBuilder();builder.Logging.ClearProviders();builder.Logging.AddConsole().SetMinimumLevel(LogLevel.Error);
        builder.WebHost.ConfigureKestrel(k=>k.Listen(IPAddress.Loopback,0));
        void Jwt(JwtBearerOptions jwt)
        {
            jwt.MapInboundClaims=false;jwt.TokenValidationParameters=new TokenValidationParameters
            {ValidateIssuer=true,ValidIssuer="https://intake-issuer.invalid",ValidateAudience=true,ValidAudience="intake-users",
                ValidateLifetime=true,ValidateIssuerSigningKey=true,IssuerSigningKey=new RsaSecurityKey(UserKey),RoleClaimType="role",NameClaimType="sub",ClockSkew=TimeSpan.Zero};
        }
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(JwtBearerDefaults.AuthenticationScheme,Jwt)
            .AddJwtBearer(RuoyuAdminApiAuthorization.SchemeName,jwt=>{Jwt(jwt);jwt.TokenValidationParameters.ValidAudience="intake-admin";});
        builder.Services.AddAuthorization(MistakeApiAuthorization.AddMistakeApiPolicy);
        builder.Services.AddDbContext<MistakeDbContext>(o=>o.UseNpgsql(Connection).AddInterceptors(Fault));
        builder.Services.AddScoped<IMistakeItemRepository,MistakeItemRepository>();builder.Services.AddScoped<IMistakePracticeRepository,MistakePracticeRepository>();
        builder.Services.AddScoped<IProcessLogRepository,ProcessLogRepository>();builder.Services.AddScoped<IUnitOfWork,UnitOfWork>();
        builder.Services.AddScoped<IMistakeDomainService,MistakeDomainService>();
        builder.Services.AddSingleton(new SourceHandoffOptions(HandoffsEnabled));builder.Services.AddSingleton(new SourceHandoffClient(SnapshotHttp,Identity));
        builder.Services.AddSingleton(new SourceReturnOptions(ReturnsEnabled));builder.Services.AddSingleton(new SourceReturnClient(SnapshotHttp,Identity));
        builder.Services.AddSingleton(new SourceIntakeOptions(Enabled));builder.Services.AddSingleton(new SourceTargetOptions(TargetsEnabled));builder.Services.AddSingleton(Snapshots);builder.Services.AddSingleton(Oss.Object);
        builder.Services.AddSingleton(Analyzer.Object);builder.Services.AddSingleton(Options.Create(new VlAnalyzerOptions{ApiKey=ConfiguredVl ? "fixture" : ""}));
        builder.Services.AddScoped<SourceIntakeService>();builder.Services.AddScoped<ISourceIntakeService>(s=>s.GetRequiredService<SourceIntakeService>());
        builder.Services.AddScoped<ISourceTargetService>(s=>s.GetRequiredService<SourceIntakeService>());
        builder.Services.AddScoped<ISourceHandoffService>(s=>s.GetRequiredService<SourceIntakeService>());
        builder.Services.AddScoped<ISourceReturnService>(s=>s.GetRequiredService<SourceIntakeService>());
        builder.Services.AddScoped<IMistakeSourceGuard>(s=>s.GetRequiredService<SourceIntakeService>());
        var studentClient=new StudentHttpClient(SnapshotHttp,NullLogger<StudentHttpClient>.Instance);
        builder.Services.AddSingleton<IUploadSourceGateway>(new StudentUploadSourceGateway(studentClient,new StudentCapabilityOptions{Enabled=true}));
        builder.Services.AddSingleton(new StudentCapabilityOptions{Enabled=true});
        builder.Services.AddSingleton(new MistakeApiResourceAuthOptions{Enabled=resource});builder.Services.AddScoped<IMistakeApiResourceAuthorizationService,MistakeApiResourceAuthorizationService>();
        builder.Services.AddScoped<IReanalyzeJobRepository,ReanalyzeJobRepository>();builder.Services.AddScoped<IReanalyzeJobService,ReanalyzeJobService>();
        if(handoffWorker)builder.Services.AddHostedService<SourceHandoffWorker>();
        App=builder.Build();App.UseAuthentication();App.UseAuthorization();App.MapMistakeHttpEndpoints(strict);await App.StartAsync();
        Client=new HttpClient(new HttpClientHandler{UseProxy=false}){BaseAddress=new Uri(App.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single())};
    }
    internal async Task<HttpResponseMessage> SendAsync(HttpMethod method,string path,object? body=null,string? token=null)
    {
        using var request=new HttpRequestMessage(method,path){Content=body==null ? null : JsonContent.Create(body)};
        if(token!=null)request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",token);
        return await Client!.SendAsync(request);
    }
    internal async Task<string> BackupAsync()
    {var container=Environment.GetEnvironmentVariable("MISTAKE_INTAKE_TEST_PG_CONTAINER") ?? throw new InvalidOperationException("Owned PostgreSQL container required.");return await ProcessAsync(["exec",container,"pg_dump","-U","postgres","-d",Name]);}
    internal async Task RestoreAsync(string dump)
    {var container=Environment.GetEnvironmentVariable("MISTAKE_INTAKE_TEST_PG_CONTAINER")!;await ProcessAsync(["exec","-i",container,"psql","-U","postgres","-v","ON_ERROR_STOP=1","-d",Name],dump);}
    private static async Task<string> ProcessAsync(string[] arguments,string? input=null)
    {
        var start=new ProcessStartInfo("docker"){RedirectStandardOutput=true,RedirectStandardError=true,RedirectStandardInput=input!=null};
        foreach(var arg in arguments)start.ArgumentList.Add(arg);using var process=Process.Start(start)!;
        if(input!=null){await process.StandardInput.WriteAsync(input);process.StandardInput.Close();}
        var output=process.StandardOutput.ReadToEndAsync();var error=process.StandardError.ReadToEndAsync();await process.WaitForExitAsync();await error;
        Assert.Equal(0,process.ExitCode);return await output;
    }
    internal async Task AssertRetainedAsync(StudentRecord record)
    {
        Assert.Empty(SideEffects);Assert.Empty(Source.Deletes);
        Assert.All(Source.Requests,request=>Assert.True(request.Method=="GET" || (request.Method=="PUT" && request.Path.Contains("/snapshots/",StringComparison.Ordinal))));
        Assert.DoesNotContain(Oss.Invocations,call=>call.Method.Name.Contains("Upload",StringComparison.Ordinal) || call.Method.Name.Contains("Copy",StringComparison.Ordinal) || call.Method.Name.Contains("Delete",StringComparison.Ordinal));
        await using var source=Source.Context();Assert.True(await source.ImageUploadRecords.AnyAsync(r=>r.Id==record.Id));
        Assert.Equal(1,await source.UploadSourceSnapshots.CountAsync(s=>s.SourceUploadId==record.Id));
        var pin=await source.UploadSourceSnapshots.SingleAsync(s=>s.SourceUploadId==record.Id);
        using var manifest=JsonDocument.Parse(pin.Manifest);
        foreach(var image in manifest.RootElement.GetProperty("images").EnumerateArray())
        {
            await using var stream=await Source.Files.DownloadAsync(image.GetProperty("path").GetString()!);
            using var memory=new MemoryStream();await stream.CopyToAsync(memory);var bytes=memory.ToArray();
            Assert.Equal(image.GetProperty("length").GetInt64(),bytes.LongLength);
            Assert.Equal(image.GetProperty("sha256").GetString(),Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        }
    }
    public async ValueTask DisposeAsync()
    {
        Client?.Dispose();if(App!=null)await App.DisposeAsync();SnapshotHttp.Dispose();UserKey.Dispose();
        await using var connection=new NpgsqlConnection(_admin);await connection.OpenAsync();await using var command=new NpgsqlCommand($"DROP DATABASE \"{Name}\" WITH(FORCE)",connection);await command.ExecuteNonQueryAsync();await Source.DisposeAsync();
    }
    internal sealed class CommitFault : DbTransactionInterceptor
    {
        internal int LoseCommitNumber {get;set;}
        internal int CommitCount {get;set;}
        internal bool FailBeforeCommit {get;set;}
        internal int FailCommitNumber {get;set;}
        internal Action? CancelBeforeCommit {get;set;}
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,TransactionEventData data,InterceptionResult result,CancellationToken ct=default)
        {var cancel=CancelBeforeCommit;CancelBeforeCommit=null;cancel?.Invoke();ct.ThrowIfCancellationRequested();if(FailBeforeCommit || FailCommitNumber==CommitCount+1){FailCommitNumber=0;FailBeforeCommit=false;throw new IOException("Injected commit failure.");}return ValueTask.FromResult(result);}
        public override Task TransactionCommittedAsync(DbTransaction transaction,TransactionEndEventData data,CancellationToken ct=default)
        {CommitCount++;if(LoseCommitNumber==CommitCount)throw new IOException("Injected response loss after actual commit.");return Task.CompletedTask;}
    }
}
