namespace CasaTimo.Infrastructure.Connectors;

public class DaikinOptions
{
    /// <summary>Keycloak token endpoint per Daikin Cloud Europe.</summary>
    public string TokenEndpoint { get; set; } =
        "https://idp.prod.unicloud.eaasone.com/auth/realms/unicloud/protocol/openid-connect/token";

    public string ApiBaseUrl { get; set; } = "https://api.prod.unicloud.eaasone.com";

    public string? Username { get; set; }
    public string? Password { get; set; }

    /// <summary>Client ID OAuth2 — di solito "openid-connect" per Daikin Cloud.</summary>
    public string ClientId { get; set; } = "openid-connect";

    public int PollIntervalSeconds { get; set; } = 300;
}
