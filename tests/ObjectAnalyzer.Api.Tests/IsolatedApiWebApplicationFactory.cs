using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ObjectAnalyzer.Api.Tests;

/// <summary>Each test host owns its database, including startup recovery and related stores.</summary>
public class IsolatedApiWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(), "oa-api-tests", Guid.NewGuid().ToString("N"), "history.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Platform:DatabasePath", _databasePath);
    }
}
