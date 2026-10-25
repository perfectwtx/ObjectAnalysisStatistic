using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace ObjectAnalyzer.Api.Tests;

public sealed class AsyncSubmissionCleanupTests : IClassFixture<IsolatedApiWebApplicationFactory>
{
    private readonly IsolatedApiWebApplicationFactory _factory;

    public AsyncSubmissionCleanupTests(IsolatedApiWebApplicationFactory factory) => _factory = factory;

    [Theory]
    [InlineData(false, "{\"unknownCleanupRule\":true}")]
    [InlineData(true, "{\"unknownCleanupRule\":true}")]
    [InlineData(false, "invalid-json")]
    public async Task RejectedRules_DeleteUploadedData(bool uploadRules, string rules)
    {
        var extension = $".cleanup-{Guid.NewGuid():N}";
        using var client = _factory.CreateClient();
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("[{\"value\":1}]"), "file", $"data{extension}");
        if (uploadRules) form.Add(new StringContent(rules), "rules", "rules.json");
        else form.Add(new StringContent(rules), "rulesJson");
        using var response = await client.PostAsync("/api/analyze/async", form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(Directory.GetFiles(Path.GetTempPath(), $"oa-async-*{extension}"));
    }

    [Fact]
    public async Task AcceptedSubmission_KeepsInputUntilBackgroundJobFinishes()
    {
        using var client = _factory.CreateClient();
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("[{\"value\":1},{\"value\":2}]"), "file", "data.json");
        using var response = await client.PostAsync("/api/analyze/async", form);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var submitted = await response.Content.ReadFromJsonAsync<JsonElement>();
        var id = submitted.GetProperty("jobId").GetString();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        JsonElement snapshot;
        do
        {
            await Task.Delay(20, timeout.Token);
            snapshot = await client.GetFromJsonAsync<JsonElement>($"/api/analyze/async/{id}", timeout.Token);
        } while (snapshot.GetProperty("status").GetString() == "Running");

        Assert.Equal("Completed", snapshot.GetProperty("status").GetString());
        Assert.Equal(2, snapshot.GetProperty("processedObjects").GetInt64());
    }
}
