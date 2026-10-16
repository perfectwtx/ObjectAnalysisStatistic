using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using ObjectAnalyzer.Api;

namespace ObjectAnalyzer.Api.Tests;

public sealed class IssueEndpointTests : IClassFixture<IsolatedApiWebApplicationFactory>
{
    private readonly IsolatedApiWebApplicationFactory _factory;

    public IssueEndpointTests(IsolatedApiWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Responsibility_And_Remediation_CanBeSavedAndReadBack()
    {
        var client = _factory.CreateClient();
        var jobId = Guid.NewGuid();
        var fieldName = "Customer Name";
        var check = "null-check";

        var responsibility = new IssueEndpoints.IssueResponsibilityRequest(
            Owner: "data-team",
            Status: "InProgress",
            Comment: "API integration test");

        var responsibilityUrl = $"/api/issues/{jobId}/responsibilities/{Uri.EscapeDataString(fieldName)}/{Uri.EscapeDataString(check)}";
        var put = await client.PutAsJsonAsync(responsibilityUrl, responsibility);

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var savedResponsibility = await put.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(jobId.ToString(), savedResponsibility.GetProperty("jobId").GetString());
        Assert.Equal(fieldName, savedResponsibility.GetProperty("fieldName").GetString());
        Assert.Equal("data-team", savedResponsibility.GetProperty("owner").GetString());

        var responsibilities = await client.GetFromJsonAsync<JsonElement>(
            $"/api/issues/{jobId}/responsibilities");
        var saved = Assert.Single(responsibilities.EnumerateArray());
        Assert.Equal("InProgress", saved.GetProperty("status").GetString());

        var remediation = new IssueEndpoints.RemediationRequest(
            FieldName: fieldName,
            Check: check,
            Action: "补充空值校验",
            Status: "Planned",
            Comment: "API integration test");

        var post = await client.PostAsJsonAsync($"/api/issues/{jobId}/remediations", remediation);

        Assert.Equal(HttpStatusCode.OK, post.StatusCode);
        var savedRemediation = await post.Content.ReadFromJsonAsync<JsonElement>();
        var remediationId = savedRemediation.GetProperty("id").GetGuid();
        Assert.NotEqual(Guid.Empty, remediationId);
        Assert.Equal(fieldName, savedRemediation.GetProperty("fieldName").GetString());
        Assert.Equal("补充空值校验", savedRemediation.GetProperty("action").GetString());

        var remediations = await client.GetFromJsonAsync<JsonElement>(
            $"/api/issues/{jobId}/remediations/{Uri.EscapeDataString(fieldName)}/{Uri.EscapeDataString(check)}");
        var loadedRemediation = Assert.Single(remediations.EnumerateArray());
        Assert.Equal(remediationId, loadedRemediation.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Responsibility_InvalidRequest_Returns400()
    {
        var client = _factory.CreateClient();
        var jobId = Guid.NewGuid();
        var url = $"/api/issues/{jobId}/responsibilities/{Uri.EscapeDataString("Field")}/{Uri.EscapeDataString("Check")}";

        var response = await client.PutAsJsonAsync(
            url,
            new IssueEndpoints.IssueResponsibilityRequest(Status: ""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Remediation_InvalidRequest_Returns400()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            $"/api/issues/{Guid.NewGuid()}/remediations",
            new IssueEndpoints.RemediationRequest(
                FieldName: "Field",
                Check: "Check",
                Action: ""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
