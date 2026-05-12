using System.IO;
using System.Text.Json;
using FluentAssertions;
using MTTextClient.Tests.Infrastructure;
using Xunit;

namespace MTTextClient.Tests.Tools;

/// <summary>
/// Stage 5.3 — Smoke coverage for the local profiles.json / folders.json CRUD.
/// These tools operate on the on-disk client config and do not require any
/// bench to be reachable.  We use unique sentinel names so the test never
/// collides with the operator's real profiles.
/// </summary>
[Collection(BenchCollection.Name)]
[Trait("Category", TraitCategories.Smoke)]
public sealed class Stage53ProfilesFoldersTests
{
    private readonly McpFixture _mcp;
    public Stage53ProfilesFoldersTests(McpFixture mcp) { _mcp = mcp; }

    private const string SentinelFolder = "stage53_sentinel_folder";
    private const string SentinelProfile = "stage53_sentinel_profile";

    [SkippableFact]
    public async Task mt_profiles_add_without_confirm_is_rejected_by_gate()
    {
        Skip.If(!EnvFlags.TestingEnv, "MTC_TESTING_ENV not set.");
        var resp = await _mcp.CallTool("mt_profiles_add", new
        {
            name = SentinelProfile, address = "127.0.0.1",
            port = "9999", token = "tok", exchange = "BINANCE",
            // confirm omitted
        });
        resp.IsRpcError.Should().BeTrue();
    }

    [SkippableFact]
    public async Task mt_folders_add_without_confirm_is_rejected_by_gate()
    {
        Skip.If(!EnvFlags.TestingEnv, "MTC_TESTING_ENV not set.");
        var resp = await _mcp.CallTool("mt_folders_add", new
        {
            name = SentinelFolder,
            // confirm omitted
        });
        resp.IsRpcError.Should().BeTrue();
    }

    [SkippableFact]
    public async Task mt_profiles_full_crud_round_trip_restores_baseline()
    {
        Skip.If(!EnvFlags.TestingEnv, "MTC_TESTING_ENV not set.");

        int before = await CountProfiles();

        // Add a folder first so move can target it.
        await _mcp.CallTool("mt_folders_add", new { name = SentinelFolder, confirm = true });

        // Add the profile in root.
        var add = await _mcp.CallTool("mt_profiles_add", new
        {
            name = SentinelProfile, address = "127.0.0.1",
            port = "12345", token = "smoketoken", exchange = "BINANCE",
            confirm = true,
        });
        add.InnerSuccess.Should().BeTrue(because: "add: " + add.InnerMessage);
        (await CountProfiles()).Should().Be(before + 1);

        // Move to sentinel folder.
        var move = await _mcp.CallTool("mt_profiles_move", new
        {
            name = SentinelProfile, folder = SentinelFolder, confirm = true,
        });
        move.InnerSuccess.Should().BeTrue();

        // Edit — rename to ensure rename-collision protection works.
        var edit = await _mcp.CallTool("mt_profiles_edit", new
        {
            name = SentinelProfile, rename = SentinelProfile + "_renamed", confirm = true,
        });
        edit.InnerSuccess.Should().BeTrue();

        // Delete the renamed profile.
        var del = await _mcp.CallTool("mt_profiles_delete", new
        {
            name = SentinelProfile + "_renamed", confirm = true,
        });
        del.InnerSuccess.Should().BeTrue();
        (await CountProfiles()).Should().Be(before, because: "delete must restore the baseline");

        // Delete the sentinel folder (no profiles left in it).
        var rmFolder = await _mcp.CallTool("mt_folders_delete", new
        {
            name = SentinelFolder, confirm = true,
        });
        rmFolder.InnerSuccess.Should().BeTrue();
    }

    [SkippableFact]
    public async Task mt_profiles_delete_nonexistent_returns_structured_not_found()
    {
        Skip.If(!EnvFlags.TestingEnv, "MTC_TESTING_ENV not set.");

        var resp = await _mcp.CallTool("mt_profiles_delete", new
        {
            name = "___never_exists_sentinel___",
            confirm = true,
        });
        resp.InnerSuccess.Should().BeFalse();
        resp.InnerMessage!.Should().Contain("not_found");
    }

    [SkippableFact]
    public async Task mt_profiles_move_to_unknown_folder_returns_structured_error()
    {
        Skip.If(!EnvFlags.TestingEnv, "MTC_TESTING_ENV not set.");

        // First, create a sentinel profile.
        await _mcp.CallTool("mt_profiles_add", new
        {
            name = SentinelProfile + "_movetest", address = "127.0.0.1",
            port = "10001", token = "tok", exchange = "BINANCE", confirm = true,
        });

        var resp = await _mcp.CallTool("mt_profiles_move", new
        {
            name = SentinelProfile + "_movetest",
            folder = "never_was_a_folder_xyz",
            confirm = true,
        });
        resp.InnerSuccess.Should().BeFalse();
        resp.InnerMessage!.Should().Contain("folder_not_found");

        // Cleanup.
        await _mcp.CallTool("mt_profiles_delete", new
        {
            name = SentinelProfile + "_movetest", confirm = true,
        });
    }

    [SkippableFact]
    public async Task mt_folders_delete_with_profiles_referencing_surfaces_orphan_warning()
    {
        Skip.If(!EnvFlags.TestingEnv, "MTC_TESTING_ENV not set.");

        // Set up: folder + a profile that lives in it.
        await _mcp.CallTool("mt_folders_add", new { name = SentinelFolder + "_orphan", confirm = true });
        await _mcp.CallTool("mt_profiles_add", new
        {
            name = SentinelProfile + "_orphan", address = "127.0.0.1",
            port = "10002", token = "tok", exchange = "BINANCE",
            folder = SentinelFolder + "_orphan", confirm = true,
        });
        await _mcp.CallTool("mt_profiles_move", new
        {
            name = SentinelProfile + "_orphan",
            folder = SentinelFolder + "_orphan", confirm = true,
        });

        // Delete the folder while a profile still references it.
        var del = await _mcp.CallTool("mt_folders_delete", new
        {
            name = SentinelFolder + "_orphan", confirm = true,
        });
        del.InnerSuccess.Should().BeTrue();
        del.InnerMessage!.Should().Contain("WARNING");
        del.InnerMessage.Should().Contain("ORPHAN");

        // Cleanup the orphan profile.
        await _mcp.CallTool("mt_profiles_delete", new
        {
            name = SentinelProfile + "_orphan", confirm = true,
        });
    }

    [SkippableFact]
    public async Task mt_profiles_import_csv_missing_columns_surfaces_structured_error()
    {
        Skip.If(!EnvFlags.TestingEnv, "MTC_TESTING_ENV not set.");

        // Write a malformed CSV to a temp path.  Missing required columns.
        string path = Path.Combine(Path.GetTempPath(), $"stage53_csv_{System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.csv");
        File.WriteAllText(path, "name,address\nrowA,127.0.0.1\n");

        var resp = await _mcp.CallTool("mt_profiles_import_csv", new
        {
            path = path, confirm = true,
        });
        try
        {
            resp.InnerSuccess.Should().BeFalse();
            resp.InnerMessage!.Should().Contain("csv_missing_columns");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private async Task<int> CountProfiles()
    {
        var resp = await _mcp.CallTool("mt_profiles_list", new { });
        if (!resp.InnerSuccess) return -1;
        return resp.ParsedBody!.Value.GetProperty("data").GetProperty("Count").GetInt32();
    }
}
