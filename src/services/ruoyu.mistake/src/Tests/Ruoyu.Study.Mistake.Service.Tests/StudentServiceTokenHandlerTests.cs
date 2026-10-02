using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;
using Ruoyu.Study.Mistake.Host;
using Ruoyu.Study.Mistake.StudentClient;

namespace Ruoyu.Study.Mistake.Service.Tests;

public sealed class StudentServiceTokenHandlerTests
{

    [Theory]
    [InlineData("GET","/api/integrations/mistake/returns/mistake-return:abc/receipt",true)]
    [InlineData("POST","/api/integrations/mistake/returns/mistake-return:abc/receipt",false)]
    [InlineData("GET","/api/integrations/mistake/returns/mistake-return:abc/receipt?extra=1",false)]
    [InlineData("GET","/api/integrations/mistake/returns/mistake-return:abc/receipt/",false)]
    [InlineData("GET","/api/integrations/mistake/returns/a%2Fb/receipt",false)]
    [InlineData("GET","/api/integrations/mistake/returns/猫/receipt",false)]
    [InlineData("GET","/api/integrations/mistake/returns//receipt",false)]
    public async Task HistoricalReceiptSignsOnlyExactGetOperationAndNoQueryAsync(string method,string path,bool allowed)
    {
        using var keys=new KeyFile();var calls=0;
        using var handler=new StudentServiceTokenHandler(keys.Options()){InnerHandler=new Sender((request,_)=>{calls++;var token=keys.Validate(request.Headers.Authorization?.Parameter);Assert.Equal("uploads.return",token.Claims.Single(c=>c.Type=="scope").Value);return Task.FromResult(Json(HttpStatusCode.OK,"{}"));})};
        using var http=new HttpClient(handler){BaseAddress=new Uri("https://student.test")};using var request=new HttpRequestMessage(new HttpMethod(method),path);request.Headers.Authorization=new AuthenticationHeaderValue("Bearer","user-canary");
        if(allowed){using var response=await http.SendAsync(request);Assert.Equal(1,calls);}else{await Assert.ThrowsAsync<InvalidOperationException>(()=>http.SendAsync(request));Assert.Equal(0,calls);Assert.Equal("user-canary",request.Headers.Authorization!.Parameter);}
    }

    [Fact]
    public async Task ActualTypedClientUsesServiceIdentityForWorkerCallbacksAndAccountResolutionAsync()
    {
        using var keys = new KeyFile();
        var seen = new ConcurrentQueue<(string Path, string Scope, string Subject)>();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(keys.Options());
        services.AddTransient<StudentServiceTokenHandler>();
        services.AddStudentHttpClient("https://student.test");
        services.AddHttpClient<IStudentHttpClient, StudentHttpClient>()
            .AddHttpMessageHandler<StudentServiceTokenHandler>()
            .ConfigurePrimaryHttpMessageHandler(() => new Sender((request, _) =>
            {
                var token = keys.Validate(request.Headers.Authorization?.Parameter);
                seen.Enqueue((request.RequestUri!.AbsolutePath, token.Claims.Single(claim => claim.Type == "scope").Value, token.Subject));
                var data = request.RequestUri.AbsolutePath switch
                {
                    "/api/uploads/processing/scan" => "{\"items\":[],\"hasMore\":false,\"nextCursor\":null}",
                    "/api/students/student-1/uploads/returned" => "{\"success\":true,\"replayed\":true}",
                    var path when path.EndsWith("/remove-processed") => "{\"success\":false,\"errorKind\":\"unknown_record\"}",
                    _ => "{\"students\":[{\"id\":\"student-1\"}]}"
                };
                return Task.FromResult(Json(HttpStatusCode.OK, "{\"success\":true,\"data\":" + data + "}"));
            }));
        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IStudentHttpClient>();
        Assert.Empty((await client.ScanProcessingUploadRecordsAsync(null, 10)).Items!);
        Assert.True((await client.CreateReturnedRecordAsync("student-1", [], null, null, "operation-1")).Replayed);
        Assert.Equal("unknown_record", (await client.RemoveProcessedImagesAsync(Guid.NewGuid().ToString(), ["uploads/a"])).ErrorKind);
        Assert.Equal("student-1", Assert.Single((await client.GetStudentsByIdentityAccountIdStrictAsync("account-1")).Students).Id);
        Assert.Equal(new[] { "uploads.scan", "uploads.return", "uploads.remove", "accounts.read" }, seen.Select(value => value.Scope));
        Assert.All(seen, value => Assert.Equal("ruoyu.mistake", value.Subject));
        Assert.Equal("/api/integrations/mistake/accounts/account-1/students", seen.Last().Path);
    }

    [Fact]
    public async Task ConcurrentRequestsReplaceUserTokensWithFreshScopedJwtAndPreserveQueryAsync()
    {
        using var keys = new KeyFile();
        var seen = new ConcurrentBag<JwtSecurityToken>();
        using var handler = new StudentServiceTokenHandler(keys.Options())
        {
            InnerHandler = new Sender((request, _) =>
            {
                Assert.Equal("?pageSize=10&cursor=a%2Fb", request.RequestUri!.Query);
                seen.Add(keys.Validate(request.Headers.Authorization?.Parameter));
                return Task.FromResult(Json(HttpStatusCode.OK, "{}"));
            })
        };
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://student.test") };
        await Task.WhenAll(Enumerable.Range(0, 24).Select(async index =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/uploads/processing/scan?pageSize=10&cursor=a%2Fb");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "user-canary-" + index);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }));
        Assert.Equal(24, seen.Count);
        Assert.Equal(24, seen.Select(token => token.Id).Distinct().Count());
        Assert.All(seen, token =>
        {
            Assert.Equal("key-1", token.Header.Kid);
            Assert.Equal(SecurityAlgorithms.RsaSha256, token.Header.Alg);
            Assert.Equal("uploads.scan", token.Claims.Single(claim => claim.Type == "scope").Value);
            var issued = long.Parse(token.Claims.Single(claim => claim.Type == "iat").Value);
            Assert.Equal(60, new DateTimeOffset(token.ValidTo).ToUnixTimeSeconds() - issued);
        });
    }

    [Fact]
    public async Task CompatibilityDisabledHandlerKeepsOriginalRouteAndCancellationPreventsSendingAsync()
    {
        var disabled = StudentServiceAuthenticationOptions.Read(new ConfigurationBuilder().Build(), "http://student.test");
        var calls = 0;
        using var handler = new StudentServiceTokenHandler(disabled) { InnerHandler = new Sender((request, _) =>
        {
            calls++;
            Assert.Equal("/api/accounts/account-1/students", request.RequestUri!.AbsolutePath);
            Assert.Null(request.Headers.Authorization);
            return Task.FromResult(Json(HttpStatusCode.OK, "{}"));
        }) };
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://student.test") };
        using var response = await client.GetAsync("/api/accounts/account-1/students");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetAsync("/api/accounts/account-1/students", canceled.Token));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task CancellationTimeoutAndUnsupportedRoutesNeverRetryWithoutAuthenticationAsync()
    {
        using var keys = new KeyFile();
        var calls = 0;
        using var handler = new StudentServiceTokenHandler(keys.Options()) { InnerHandler = new Sender(async (request, token) =>
        {
            calls++;
            keys.Validate(request.Headers.Authorization?.Parameter);
            await Task.Delay(Timeout.Infinite, token);
            return Json(HttpStatusCode.OK, "{}");
        }) };
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://student.test"), Timeout = TimeSpan.FromMilliseconds(150) };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetAsync("/api/uploads/processing/scan"));
        Assert.Equal(1, calls);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/unregistered-route");
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendAsync(request));
        Assert.Equal(1, calls);
        using var callerCancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetAsync("/api/uploads/processing/scan", callerCancellation.Token));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task SigningFailurePreventsAnyNetworkRequestAsync()
    {
        using var keys = new KeyFile();
        var options = keys.Options();
        // Fault injection corrupts the startup signing snapshot after configuration validation.
        typeof(StudentServiceAuthenticationOptions).GetProperty("PrivateKey", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(options, default(RSAParameters));
        var calls = 0;
        using var handler = new StudentServiceTokenHandler(options) { InnerHandler = new Sender((_, _) =>
        { calls++; return Task.FromResult(Json(HttpStatusCode.OK, "{}")); }) };
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://student.test") };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAsync("/api/uploads/processing/scan"));
        Assert.Equal("Student service authentication signing failed.", error.Message);
        Assert.Null(error.InnerException);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task ProviderAuthRejectionRetainsUnknownCallbackClassificationAsync(HttpStatusCode status)
    {
        using var keys = new KeyFile();
        var calls = 0;
        using var handler = new StudentServiceTokenHandler(keys.Options()) { InnerHandler = new Sender((_, _) =>
        { calls++; return Task.FromResult(Json(status, "{\"success\":false,\"message\":\"Mistake service authentication required.\"}")); }) };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://student.test") };
        var client = new StudentHttpClient(http, NullLogger<StudentHttpClient>.Instance);
        Assert.Equal(CreateReturnedRecordStatus.Unknown, (await client.CreateReturnedRecordAsync("student-1", [], null, null, "operation-1")).Status);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void ConfigurationRequiresPrivateSecretAndExplicitSecureTransport()
    {
        using var keys = new KeyFile();
        Assert.Throws<InvalidOperationException>(() => keys.Options("http://student.test"));
        Assert.True(keys.Options("http://student.test", values => values[StudentServiceAuthenticationOptions.Section + ":AllowInsecureHttp"] = "true").Enabled);
        foreach (var mutation in new Action<Dictionary<string, string?>>[]
        {
            values => values[StudentServiceAuthenticationOptions.Section + ":Enabled"] = "invalid",
            values => values[StudentServiceAuthenticationOptions.Section + ":AllowInsecureHttp"] = "invalid",
            values => values[StudentServiceAuthenticationOptions.Section + ":Issuer"] = "",
            values => values[StudentServiceAuthenticationOptions.Section + ":Audience"] = "",
            values => values[StudentServiceAuthenticationOptions.Section + ":KeyId"] = "",
            values => values[StudentServiceAuthenticationOptions.Section + ":PrivateKeyPath"] = "missing-key-canary",
            values => values[StudentServiceAuthenticationOptions.Section + ":PrivateKeyPath"] = keys.PublicPath,
            values => values[StudentServiceAuthenticationOptions.Section + ":PrivateKeyPath"] = keys.WeakPath
        })
        {
            var error = Assert.Throws<InvalidOperationException>(() => keys.Options(mutate: mutation));
            Assert.DoesNotContain(keys.Directory, error.ToString()); Assert.DoesNotContain("BEGIN", error.ToString());
        }
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(keys.PrivatePath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
            Assert.Throws<InvalidOperationException>(() => keys.Options());
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
    private sealed class Sender(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken); }
    private sealed class KeyFile : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "mistake-service-key-" + Guid.NewGuid().ToString("N"));
        public string PrivatePath => Path.Combine(Directory, "private.pem");
        public string PublicPath => Path.Combine(Directory, "public.pem");
        public string WeakPath => Path.Combine(Directory, "weak.pem");
        private readonly RSA _rsa = RSA.Create(2048);
        public KeyFile()
        {
            System.IO.Directory.CreateDirectory(Directory);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.WriteAllText(PrivatePath, _rsa.ExportPkcs8PrivateKeyPem());
            File.WriteAllText(PublicPath, _rsa.ExportSubjectPublicKeyInfoPem());
            using var weak = RSA.Create(1024); File.WriteAllText(WeakPath, weak.ExportPkcs8PrivateKeyPem());
            if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(PrivatePath, UnixFileMode.UserRead | UnixFileMode.UserWrite); File.SetUnixFileMode(WeakPath, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
        }
        public StudentServiceAuthenticationOptions Options(string url = "https://student.test", Action<Dictionary<string, string?>>? mutate = null)
        {
            var values = new Dictionary<string, string?> { [StudentServiceAuthenticationOptions.Section + ":Enabled"] = "true", [StudentServiceAuthenticationOptions.Section + ":KeyId"] = "key-1", [StudentServiceAuthenticationOptions.Section + ":PrivateKeyPath"] = PrivatePath };
            mutate?.Invoke(values);
            return StudentServiceAuthenticationOptions.Read(new ConfigurationBuilder().AddInMemoryCollection(values).Build(), url);
        }
        public JwtSecurityToken Validate(string? encoded)
        {
            Assert.NotNull(encoded);
            var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
            handler.ValidateToken(encoded, new TokenValidationParameters { ValidIssuer = "urn:ruoyu:mistake", ValidAudience = "urn:ruoyu:student-integration", IssuerSigningKey = new RsaSecurityKey(_rsa.ExportParameters(false)), ClockSkew = TimeSpan.FromSeconds(5), ValidAlgorithms = [SecurityAlgorithms.RsaSha256] }, out var verified);
            return (JwtSecurityToken)verified;
        }
        public void Dispose() { _rsa.Dispose(); System.IO.Directory.Delete(Directory, true); }
    }
}
