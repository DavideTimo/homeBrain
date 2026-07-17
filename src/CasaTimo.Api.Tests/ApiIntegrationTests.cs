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

    // ── Cameras ──────────────────────────────────────────────────────────────

    private async Task<string> GetTokenAsync()
    {
        var loginResp = await _client.PostAsJsonAsync("/api/auth/token", new { Password = "test_admin" });
        loginResp.EnsureSuccessStatusCode();
        var json = await loginResp.Content.ReadFromJsonAsync<Dictionary<string, string>>();
        return json!["token"];
    }

    [Fact]
    public async Task GetCameras_ReturnsOk()
    {
        var response = await _client.GetAsync("/api/cameras");
        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task PostCamera_WithoutToken_ReturnsUnauthorized()
    {
        var response = await _client.PostAsJsonAsync("/api/cameras", new
        {
            Name = "ingresso",
            Location = "esterna",
            Host = "192.168.1.50",
            RtspPort = 554,
            RtspPath = "/h264Preview_01_main",
            RtspSubPath = (string?)null,
            Username = "admin",
            Password = "secret",
            Enabled = true
        });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task PostCamera_WithValidToken_CreatesCameraAndHidesPassword()
    {
        var token = await GetTokenAsync();
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/cameras");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        req.Content = JsonContent.Create(new
        {
            Name = "giardino",
            Location = "esterna",
            Host = "192.168.1.51",
            RtspPort = 554,
            RtspPath = "/h264Preview_01_main",
            RtspSubPath = (string?)null,
            Username = "admin",
            Password = "secret",
            Enabled = true
        });

        var resp = await _client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        var dto = await resp.Content.ReadFromJsonAsync<CameraDto>();
        Assert.NotNull(dto);
        Assert.Equal("giardino", dto!.Name);
        Assert.True(dto.HasPassword);
        Assert.DoesNotContain("secret", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task PutThenDeleteCamera_Works()
    {
        var token = await GetTokenAsync();

        var createReq = new HttpRequestMessage(HttpMethod.Post, "/api/cameras");
        createReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        createReq.Content = JsonContent.Create(new
        {
            Name = "cantina",
            Location = "interna",
            Host = "192.168.1.52",
            RtspPort = 554,
            RtspPath = "/stream1",
            RtspSubPath = (string?)null,
            Username = (string?)null,
            Password = (string?)null,
            Enabled = true
        });
        var createResp = await _client.SendAsync(createReq);
        createResp.EnsureSuccessStatusCode();
        var created = await createResp.Content.ReadFromJsonAsync<CameraDto>();

        var putReq = new HttpRequestMessage(HttpMethod.Put, $"/api/cameras/{created!.Id}");
        putReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        putReq.Content = JsonContent.Create(new
        {
            Name = "cantina",
            Location = "interna",
            Host = "192.168.1.52",
            RtspPort = 554,
            RtspPath = "/stream1",
            RtspSubPath = (string?)null,
            Username = (string?)null,
            Password = (string?)null,
            Enabled = false
        });
        var putResp = await _client.SendAsync(putReq);
        putResp.EnsureSuccessStatusCode();
        var updated = await putResp.Content.ReadFromJsonAsync<CameraDto>();
        Assert.False(updated!.Enabled);

        var delReq = new HttpRequestMessage(HttpMethod.Delete, $"/api/cameras/{created.Id}");
        delReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        var delResp = await _client.SendAsync(delReq);
        Assert.Equal(HttpStatusCode.NoContent, delResp.StatusCode);

        var getResp = await _client.GetAsync($"/api/cameras/{created.Id}");
        Assert.Equal(HttpStatusCode.NotFound, getResp.StatusCode);
    }

    [Fact]
    public async Task TestCamera_WithoutToken_ReturnsUnauthorized()
    {
        var response = await _client.PostAsJsonAsync("/api/cameras/test", new
        {
            Host = "192.168.1.50",
            RtspPort = 554,
            RtspPath = "/h264Preview_01_main",
            Username = (string?)null,
            Password = (string?)null
        });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TestCamera_WithValidToken_ReturnsResultWithoutCrashing()
    {
        var token = await GetTokenAsync();
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/cameras/test");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        req.Content = JsonContent.Create(new
        {
            Host = "192.0.2.1", // TEST-NET-1, non instradato: fallisce rapido invece di attendere il timeout
            RtspPort = 554,
            RtspPath = "/h264Preview_01_main",
            Username = (string?)null,
            Password = (string?)null
        });

        var resp = await _client.SendAsync(req);
        // Non deve mai restituire 500: successo o fallimento gestito vanno entrambi
        // codificati in CameraTestResult.Success, non in uno status code di errore.
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var result = await resp.Content.ReadFromJsonAsync<CameraTestResult>();
        Assert.NotNull(result);
        Assert.False(result!.Success);
    }

    private record CameraDto(
        string Id, string Name, string Location, string Host, int RtspPort,
        string RtspPath, string? RtspSubPath, string? Username, bool HasPassword,
        bool Enabled, DateTime CreatedAt, DateTime UpdatedAt);

    private record CameraTestResult(bool Success, string? Error, string? PreviewBase64, long ElapsedMs);
}
