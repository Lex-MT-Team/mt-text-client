using FluentAssertions;
using MTTextClient.Tests.Infrastructure;
using Xunit;

namespace MTTextClient.Tests.Tools;

/// <summary>
/// Vault tools — covers list / store / get / delete against the dev-mode
/// HashiCorp Vault on 127.0.0.1:8200.  See docs/vault-bootstrap.md.  With a
/// real token configured (via MTC_VAULT_TOKEN or VAULT_TOKEN env var), the
/// round-trip is fully exercised.  When no token is configured the tests
/// skip cleanly — they document the bootstrap requirement instead of
/// leaving the suite red on fresh checkouts.
/// </summary>
[Collection(McpCollection.Name)]
public sealed class VaultTests
{
    private readonly McpFixture _mcp;
    public VaultTests(McpFixture mcp) { _mcp = mcp; }

    private static string? ResolveDevToken()
        => Environment.GetEnvironmentVariable("MTC_VAULT_TOKEN")
           ?? Environment.GetEnvironmentVariable("VAULT_TOKEN");

    [SkippableFact]
    [Trait("Category", TraitCategories.Smoke)]
    public async Task mt_vault_list_profiles_succeeds_with_real_token()
    {
        Skip.If(!EnvFlags.TestingEnv, "MTC_TESTING_ENV not set; Vault tests require an external Vault");
        string? token = ResolveDevToken();
        Skip.If(string.IsNullOrEmpty(token),
            "No MTC_VAULT_TOKEN/VAULT_TOKEN; see docs/vault-bootstrap.md for dev-mode setup.");

        var resp = await _mcp.CallTool("mt_vault_list_profiles",
            new { vault_token = token!, vault_addr = "http://127.0.0.1:8200" },
            timeout: TimeSpan.FromSeconds(15));

        resp.IsRpcError.Should().BeFalse();
        // The list call must NOT return an error envelope — either profiles[] is
        // present (possibly empty) or the call surfaces a structured HTTP error.
        var body = resp.ParsedBody!.Value;
        bool hasError = body.TryGetProperty("error", out _);
        hasError.Should().BeFalse(because: "valid token: response should not contain an 'error' field");
    }

    [SkippableFact]
    [Trait("Category", TraitCategories.Smoke)]
    public async Task mt_vault_delete_profile_without_confirm_is_rejected_by_gate()
    {
        Skip.If(!EnvFlags.TestingEnv, "MTC_TESTING_ENV not set.");
        var resp = await _mcp.CallTool("mt_vault_delete_profile", new
        {
            name = "smoke-only-should-not-fire",
            // confirm intentionally omitted
        });
        resp.IsRpcError.Should().BeTrue(
            because: "destructive vault tool without confirm must be rejected by the registry gate");
        // The error envelope's 'message' field cites the missing confirm.
        resp.Envelope.GetProperty("message").GetString()!.Should().Contain("confirm",
            because: "the rejection message must cite the missing confirm field");
    }

    [SkippableFact]
    [Trait("Category", TraitCategories.Smoke)]
    public async Task mt_vault_round_trip_store_get_delete_uses_real_dev_vault()
    {
        Skip.If(!EnvFlags.TestingEnv, "MTC_TESTING_ENV not set.");
        string? token = ResolveDevToken();
        Skip.If(string.IsNullOrEmpty(token),
            "No MTC_VAULT_TOKEN/VAULT_TOKEN; see docs/vault-bootstrap.md.");

        string sentinel = $"vault-roundtrip-smoke-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
        string apiKey   = "sentinel-key-" + Guid.NewGuid().ToString("N").Substring(0, 8);
        string apiSec   = "sentinel-sec-" + Guid.NewGuid().ToString("N").Substring(0, 8);

        try
        {
            // 1) Store.
            var storeResp = await _mcp.CallTool("mt_vault_store_profile", new
            {
                name       = sentinel,
                api_key    = apiKey,
                api_secret = apiSec,
                vault_token = token!,
                vault_addr  = "http://127.0.0.1:8200",
            });
            storeResp.IsRpcError.Should().BeFalse();
            var storeBody = storeResp.ParsedBody!.Value;
            storeBody.TryGetProperty("error", out _).Should().BeFalse();
            storeBody.GetProperty("status").GetString().Should().Be("ok");

            // 2) Get — must return the same key/secret we just stored.
            var getResp = await _mcp.CallTool("mt_vault_get_profile", new
            {
                name        = sentinel,
                vault_token = token!,
                vault_addr  = "http://127.0.0.1:8200",
            });
            getResp.IsRpcError.Should().BeFalse();
            var getBody = getResp.ParsedBody!.Value;
            getBody.GetProperty("api_key").GetString().Should().Be(apiKey,
                because: "get must round-trip the api_key we stored");
            getBody.GetProperty("api_secret").GetString().Should().Be(apiSec,
                because: "get must round-trip the api_secret we stored");

            // 3) Delete — confirm=true required.
            var delResp = await _mcp.CallTool("mt_vault_delete_profile", new
            {
                name        = sentinel,
                confirm     = true,
                vault_token = token!,
                vault_addr  = "http://127.0.0.1:8200",
            });
            delResp.IsRpcError.Should().BeFalse();
            delResp.ParsedBody!.Value.GetProperty("status").GetString().Should().Be("deleted");

            // 4) Get after delete — must surface profile_not_found.
            var get2Resp = await _mcp.CallTool("mt_vault_get_profile", new
            {
                name        = sentinel,
                vault_token = token!,
                vault_addr  = "http://127.0.0.1:8200",
            });
            get2Resp.IsRpcError.Should().BeFalse();
            var get2Body = get2Resp.ParsedBody!.Value;
            string? err = get2Body.TryGetProperty("error", out var e) ? e.GetString() : null;
            err.Should().NotBeNull();
            err!.Should().Contain("profile_not_found");
        }
        finally
        {
            // Best-effort cleanup if anything bailed mid-round-trip.
            try
            {
                await _mcp.CallTool("mt_vault_delete_profile", new
                {
                    name        = sentinel,
                    confirm     = true,
                    vault_token = token!,
                    vault_addr  = "http://127.0.0.1:8200",
                });
            }
            catch { /* ignore */ }
        }
    }
}
