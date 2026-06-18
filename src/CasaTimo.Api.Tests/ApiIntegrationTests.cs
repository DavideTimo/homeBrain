using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace CasaTimo.Api.Tests;

public class ApiIntegrationTests : IClassFixture<TestApiFactory>
{
    private readonly HttpClient _client;

    public ApiIntegrationTests(TestApiFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Health_ReturnsOk()
    {
        var response = await _client.GetAsync("/health");
        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Root_ReturnsOk()
    {
        var response = await _client.GetAsync("/");
        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Login_WrongPassword_ReturnsUnauthorized()
    {
        var response = await _client.PostAsJsonAsync("/api/auth/token", new { Password = "wrong" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Login_CorrectPassword_ReturnsToken()
    {
        var response = await _client.PostAsJsonAsync("/api/auth/token", new { Password = "test_admin" });
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();
        Assert.NotNull(json);
        Assert.True(json.ContainsKey("token"), "Response should contain a 'token' field");
        Assert.False(string.IsNullOrEmpty(json["token"]));
    }

    [Fact]
    public async Task GetConnectors_ReturnsOk()
    {
        var response = await _client.GetAsync("/api/connectors");
        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task PutConnector_WithoutToken_ReturnsUnauthorized()
    {
        var response = await _client.PutAsJsonAsync("/api/connectors/test", new { });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task PutConnector_WithValidToken_ReturnsOk()
    {
        // Obtain token
        var loginResp = await _client.PostAsJsonAsync("/api/auth/token", new { Password = "test_admin" });
        loginResp.EnsureSuccessStatusCode();
        var json = await loginResp.Content.ReadFromJsonAsync<Dictionary<string, string>>();
        var token = json!["token"];

        // Send PUT with Bearer token and valid JSON body
        var req = new HttpRequestMessage(HttpMethod.Put, "/api/connectors/viessmann");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        req.Content = new StringContent("{\"host\":\"viessmann.example.com\"}",
            System.Text.Encoding.UTF8, "application/json");

        var resp = await _client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }
}
