using System.IO;
using System.Text.Json;
using FluentAssertions;
using MTTextClient.Tests.Infrastructure;
using Xunit;

namespace MTTextClient.Tests.LiveTrade;

/// <summary>
/// Vault profile round-trip LiveTrade — full store + list + get + delete +
/// get-after-delete round-trip against the dev-mode HashiCorp Vault on
/// 127.0.0.1:8200.
///
/// The "live" part here is the real Vault server (a Docker container running
/// `vault -dev`), not MTCore.  No bench profile is needed — Vault is a
/// client-side concern.  Token is resolved from MTC_VAULT_TOKEN or
/// VAULT_TOKEN (see docs/vault-bootstrap.md for setup).
/// </summary>
[Collection(McpCollection.Name)]
[Trait("Category", TraitCategories.LiveTrade)]
public sealed class VaultProfileRoundtripLiveTradeTests
{
    private readonly McpFixture _mcp;
    public VaultProfileRoundtripLiveTradeTests(McpFixture mcp) { _mcp = mcp; }

    private static string? ResolveDevToken()
        => Environment.GetEnvironmentVariable("MTC_VAULT_TOKEN")
           ?? Environment.GetEnvironmentVariable("VAULT_TOKEN");

    [SkippableFact]
    public async Task Store_Get_List_Delete_Roundtrip_AgainstDevVault()
    {
        Skip.IfNot(EnvFlags.LiveTrades,
            "MTC_LIVE_TRADES=1 not set — this LiveTrade mutates Vault state.");
        Skip.If(!EnvFlags.TestingEnv, "MTC_TESTING_ENV not set.");
        string? token = ResolveDevToken();
        Skip.If(string.IsNullOrEmpty(token),
            "No MTC_VAULT_TOKEN/VAULT_TOKEN; see docs/vault-bootstrap.md.");

        string sentinel = $"vault-roundtrip-livetrade-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
        string apiKey   = "lt-key-" + Guid.NewGuid().ToString("N").Substring(0, 12);
        string apiSec   = "lt-sec-" + Guid.NewGuid().ToString("N").Substring(0, 12);

        int? baselineCount = null, afterAddCount = null, finalCount = null;
        string? listError = null;
        try
        {
            // Baseline list (count may be 0 on a fresh dev vault).
            var listBefore = await _mcp.CallTool("mt_vault_list_profiles", new
            {
                vault_token = token!, vault_addr = "http://127.0.0.1:8200",
            });
            listBefore.IsRpcError.Should().BeFalse();
            if (listBefore.ParsedBody is { } lbb && lbb.TryGetProperty("count", out var c))
                baselineCount = c.GetInt32();
            else if (listBefore.ParsedBody is { } lbb2 && lbb2.TryGetProperty("error", out var le))
                listError = le.GetString();

            // 1) Store.
            var storeResp = await _mcp.CallTool("mt_vault_store_profile", new
            {
                name        = sentinel,
                api_key     = apiKey,
                api_secret  = apiSec,
                vault_token = token!,
                vault_addr  = "http://127.0.0.1:8200",
            });
            storeResp.IsRpcError.Should().BeFalse();
            storeResp.ParsedBody!.Value.GetProperty("status").GetString().Should().Be("ok");

            // 2) List again — count must have grown by 1.
            var listAfter = await _mcp.CallTool("mt_vault_list_profiles", new
            {
                vault_token = token!, vault_addr = "http://127.0.0.1:8200",
            });
            if (listAfter.ParsedBody is { } lab && lab.TryGetProperty("count", out var c2))
                afterAddCount = c2.GetInt32();
            if (baselineCount.HasValue && afterAddCount.HasValue)
                afterAddCount.Should().Be(baselineCount.Value + 1,
                    because: $"store must grow the count by 1 (before={baselineCount} after={afterAddCount})");

            // 3) Get — must round-trip the secret.
            var getResp = await _mcp.CallTool("mt_vault_get_profile", new
            {
                name        = sentinel,
                vault_token = token!,
                vault_addr  = "http://127.0.0.1:8200",
            });
            getResp.IsRpcError.Should().BeFalse();
            getResp.ParsedBody!.Value.GetProperty("api_key").GetString().Should().Be(apiKey);
            getResp.ParsedBody!.Value.GetProperty("api_secret").GetString().Should().Be(apiSec);

            // 4) Delete with confirm.
            var delResp = await _mcp.CallTool("mt_vault_delete_profile", new
            {
                name        = sentinel,
                confirm     = true,
                vault_token = token!,
                vault_addr  = "http://127.0.0.1:8200",
            });
            delResp.IsRpcError.Should().BeFalse();
            delResp.ParsedBody!.Value.GetProperty("status").GetString().Should().Be("deleted");

            // 5) Get after delete — must surface profile_not_found.
            var get2Resp = await _mcp.CallTool("mt_vault_get_profile", new
            {
                name        = sentinel,
                vault_token = token!,
                vault_addr  = "http://127.0.0.1:8200",
            });
            get2Resp.ParsedBody!.Value.GetProperty("error").GetString()
                .Should().Contain("profile_not_found");

            // 6) List final — count must be back to baseline.
            var listFinal = await _mcp.CallTool("mt_vault_list_profiles", new
            {
                vault_token = token!, vault_addr = "http://127.0.0.1:8200",
            });
            if (listFinal.ParsedBody is { } lf && lf.TryGetProperty("count", out var c3))
                finalCount = c3.GetInt32();
            if (baselineCount.HasValue && finalCount.HasValue)
                finalCount.Should().Be(baselineCount.Value);

            await WriteArtifact(new
            {
                Scenario = "VaultProfileRoundtrip",
                VaultAddr = "http://127.0.0.1:8200",
                Sentinel = sentinel,
                BaselineCount = baselineCount,
                AfterAddCount = afterAddCount,
                FinalCount = finalCount,
                ListError = listError,
                CrudPath = "store → list grow → get → delete → get not_found → list restored",
                EndedAtUtc = DateTime.UtcNow,
            });
        }
        finally
        {
            // Best-effort cleanup if anything threw before delete ran.
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

    private static async Task WriteArtifact(object record)
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "mt-test-artifacts", "vault-roundtrip");
        Directory.CreateDirectory(dir);
        string fname = $"vault_{DateTime.UtcNow:yyyyMMdd-HHmmss}.json";
        await File.WriteAllTextAsync(
            Path.Combine(dir, fname),
            JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true }));
    }
}
