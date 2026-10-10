using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ConnectedOps.Application;
using ConnectedOps.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ConnectedOps.Tests;

public sealed class AuthTests : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly WebApplicationFactory<Program> _factory;

    public AuthTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public void Demo_directory_authenticates_known_users_and_rejects_bad_passwords()
    {
        var directory = new DemoUserDirectory();

        var admin = directory.Authenticate("admin", "admin-demo");
        Assert.NotNull(admin);
        Assert.Contains(AuthRoles.Operator, admin.Roles);
        Assert.Contains(AuthRoles.Approver, admin.Roles);
        Assert.Null(directory.Authenticate("admin", "wrong-password"));
    }

    [Fact]
    public async Task Protected_endpoints_require_authentication()
    {
        var client = _factory.CreateClient();

        var dashboard = await client.GetAsync("/api/operations/dashboard");
        Assert.Equal(HttpStatusCode.Unauthorized, dashboard.StatusCode);
    }

    [Fact]
    public async Task Operator_can_run_scenario_but_cannot_approve()
    {
        var client = await CreateAuthenticatedClientAsync("operator", "operate-demo");
        await ResetAsync(client);

        var assets = await client.GetFromJsonAsync<DashboardResponse>(
            "/api/operations/dashboard",
            JsonOptions);
        var vehicle = Assert.Single(
            assets!.Assets,
            asset => asset.Name == "Vehicle 12");

        var scenario = await client.PostAsync(
            $"/api/simulation/offline/{vehicle.Id}",
            content: null);
        Assert.Equal(HttpStatusCode.OK, scenario.StatusCode);

        using var document = JsonDocument.Parse(await scenario.Content.ReadAsStringAsync());
        var incidentId = document.RootElement.GetProperty("id").GetGuid();

        var approval = await client.PostAsync(
            $"/api/incidents/{incidentId}/approve-notification",
            JsonContent.Create(new { }));
        Assert.Equal(HttpStatusCode.Forbidden, approval.StatusCode);
    }

    [Fact]
    public async Task Approver_can_approve_after_operator_opens_incident()
    {
        var operatorClient = await CreateAuthenticatedClientAsync(
            "operator",
            "operate-demo");
        await ResetAsync(operatorClient);

        var assets = await operatorClient.GetFromJsonAsync<DashboardResponse>(
            "/api/operations/dashboard",
            JsonOptions);
        var vehicle = Assert.Single(
            assets!.Assets,
            asset => asset.Name == "Vehicle 12");
        var scenario = await operatorClient.PostAsync(
            $"/api/simulation/offline/{vehicle.Id}",
            content: null);
        Assert.Equal(HttpStatusCode.OK, scenario.StatusCode);
        using var document = JsonDocument.Parse(await scenario.Content.ReadAsStringAsync());
        var incidentId = document.RootElement.GetProperty("id").GetGuid();

        var approverClient = await CreateAuthenticatedClientAsync(
            "approver",
            "approve-demo");

        var approval = await approverClient.PostAsync(
            $"/api/incidents/{incidentId}/approve-notification",
            JsonContent.Create(new { }));
        Assert.Equal(HttpStatusCode.OK, approval.StatusCode);

        using var approved = JsonDocument.Parse(await approval.Content.ReadAsStringAsync());
        Assert.Equal(
            "Jordan Approver",
            approved.RootElement
                .GetProperty("proposedAction")
                .GetProperty("approvedBy")
                .GetString());
    }

    private async Task<HttpClient> CreateAuthenticatedClientAsync(
        string username,
        string password)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue(
                "Bearer",
                await LoginAsync(client, username, password));
        return client;
    }

    private static async Task ResetAsync(HttpClient client)
    {
        var response = await client.PostAsync("/api/simulation/reset", content: null);
        response.EnsureSuccessStatusCode();
    }

    private static async Task<string> LoginAsync(
        HttpClient client,
        string username,
        string password)
    {
        var response = await client.PostAsJsonAsync(
            "/api/auth/login",
            new { username, password });
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("accessToken").GetString()!;
    }

    private sealed record DashboardResponse(IReadOnlyList<AssetResponse> Assets);
    private sealed record AssetResponse(Guid Id, string Name);
}
