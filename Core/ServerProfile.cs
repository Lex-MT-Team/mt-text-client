using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using MTShared.Types;
namespace MTTextClient.Core;

/// <summary>
/// Server connection profile — stores all data needed to connect to an MT-Core instance.
/// </summary>
public sealed class ServerProfile
{
    public string Name { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public int Port { get; set; }
    public string ClientToken { get; set; } = string.Empty;
    public ExchangeType Exchange { get; set; } = ExchangeType.BINANCE;

    /// <summary>
    /// Fleet orchestration labels (role, strategy, group, region etc.).
    /// Set at runtime via mt_connection_tag; persisted in profiles.json if present.
    /// </summary>
    public Dictionary<string, string> Tags { get; set; } = new();

    /// <summary>
    /// Derives the connection key seed from ClientToken and Exchange.
    /// Matches MTController.CoreSupervisor.GetConnectionKeySeed() exactly.
    /// </summary>
    public string GetConnectionKeySeed()
    {
        string? clientTokenB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(ClientToken));
        using SHA512? sha512 = SHA512.Create();
        byte[]? hashBytes = sha512.ComputeHash(Convert.FromBase64String(clientTokenB64));
        return $"{Exchange}|{BitConverter.ToString(hashBytes)}";
    }

    public override string ToString() => $"{Name} ({Exchange}) @ {Address}:{Port}";
}
