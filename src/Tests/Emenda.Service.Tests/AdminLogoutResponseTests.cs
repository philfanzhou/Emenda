using System.Text.Json;
using Emenda.Host.Authentication;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using SignaCore.Client.AspNetCore;

namespace Emenda.Service.Tests;

public sealed class AdminLogoutResponseTests
{
    [Fact]
    public async Task PreparedLogout_ReturnsVerifiedDestinationForTopLevelBrowserNavigation()
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        await using var body = new MemoryStream();
        context.Response.Body = body;
        const string destination = "https://identity.example/oauth2/logout?logout_handle=verified-handle";
        ISignaCoreHostedLoginResponseWriter writer = AdminConsoleResponseWriter.Instance;

        await writer.WriteLogoutPreparedAsync(context, destination, CancellationToken.None);

        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        context.Response.Headers.Location.ToString().Should().BeEmpty();
        context.Response.Headers.CacheControl.ToString().Should().Be("no-store");
        body.Position = 0;
        using var document = await JsonDocument.ParseAsync(body);
        document.RootElement.GetProperty("logoutUrl").GetString().Should().Be(destination);
    }

    [Fact]
    public async Task LocalOnlyLogout_ReturnsJsonWithoutClaimingProviderSignOut()
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        await using var body = new MemoryStream();
        context.Response.Body = body;
        ISignaCoreHostedLoginResponseWriter writer = AdminConsoleResponseWriter.Instance;

        await writer.WriteLogoutLocalOnlyAsync(context, CancellationToken.None);

        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        context.Response.Headers.CacheControl.ToString().Should().Be("no-store");
        body.Position = 0;
        using var document = await JsonDocument.ParseAsync(body);
        document.RootElement.GetProperty("outcome").GetString().Should().Be("local_only");
        document.RootElement.GetProperty("logoutUrl").ValueKind.Should().Be(JsonValueKind.Null);
    }
}
