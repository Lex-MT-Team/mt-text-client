using System;
using System.Reflection;
using MTShared.Network;
using MTShared.Network.Watchdog;
using MTShared.Types;

namespace MTTextClient.Core.Diagnostics;

/// <summary>
/// Narrow, fail-closed bridge to the pinned native packet implementation.
/// The MTShared members are intentionally internal; their names, parameter names,
/// and signatures are checked once before a diagnostic listener accepts traffic.
/// </summary>
public sealed class NativeDiagnosticVendorAdapter
{
    private const byte EncryptedMarker = 64;
    private const int MinimumWireBytes = 8;
    private const int CompressionFlagOffset = 7;
    private const int MaximumWireBytes = 64 * 1024;
    private static readonly Type VendorAssemblyAnchor = typeof(ExchangeType);

    private readonly byte[] _cryptoKey;
    private readonly Type _messageType;
    private readonly MethodInfo _pack;
    private readonly MethodInfo _unpack;
    private readonly FieldInfo _messageTypeField;
    private readonly FieldInfo _priorityField;
    private readonly FieldInfo _requestIdField;
    private readonly FieldInfo _bodyField;
    private readonly MethodInfo _claim;
    private readonly MethodInfo _release;
    private readonly MethodInfo _encrypt;
    private readonly MethodInfo _decrypt;

    private NativeDiagnosticVendorAdapter(
        byte[] cryptoKey,
        Type messageType,
        MethodInfo pack,
        MethodInfo unpack,
        FieldInfo messageTypeField,
        FieldInfo priorityField,
        FieldInfo requestIdField,
        FieldInfo bodyField,
        MethodInfo claim,
        MethodInfo release,
        MethodInfo encrypt,
        MethodInfo decrypt)
    {
        _cryptoKey = cryptoKey;
        _messageType = messageType;
        _pack = pack;
        _unpack = unpack;
        _messageTypeField = messageTypeField;
        _priorityField = priorityField;
        _requestIdField = requestIdField;
        _bodyField = bodyField;
        _claim = claim;
        _release = release;
        _encrypt = encrypt;
        _decrypt = decrypt;
    }

    /// <summary>Builds the bridge only when the exact pinned helper surface is present.</summary>
    public static bool TryCreate(string? connectionKeySeed, out NativeDiagnosticVendorAdapter? adapter)
    {
        adapter = null;
        if (string.IsNullOrWhiteSpace(connectionKeySeed))
        {
            return false;
        }

        try
        {
            Assembly assembly = VendorAssemblyAnchor.Assembly;
            const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            Type? pool = assembly.GetType("MTShared.AES256EncryptorPool", throwOnError: false);
            Type? encryptor = assembly.GetType("MTShared.AES256Encryptor", throwOnError: false);
            Type? message = assembly.GetType("MTShared.Structs.NetworkMessage", throwOnError: false);
            Type? utils = assembly.GetType("MTShared.Utils.MTSharedUtils", throwOnError: false);
            Type nativeMessageType = typeof(NetworkMessageType);
            if (pool is null || encryptor is null || message is null || utils is null || nativeMessageType is null)
            {
                return false;
            }

            MethodInfo? generateKey = pool.GetMethod("GenerateCryptoKey", Static, new[] { typeof(string) });
            MethodInfo? claim = pool.GetMethod("Claim", Static, new[] { typeof(byte[]) });
            MethodInfo? release = pool.GetMethod("Release", Static, new[] { encryptor });
            MethodInfo? encrypt = encryptor.GetMethod("Encrypt", Instance, new[] { typeof(byte[]), typeof(bool), typeof(uint) });
            MethodInfo? decrypt = utils.GetMethod("TryDecrypt", Static, new[] { nativeMessageType, typeof(byte[]), typeof(byte[]), typeof(byte[]).MakeByRefType() });
            MethodInfo? pack = message.GetMethod("Pack", Instance, new[] { typeof(int) });
            MethodInfo? unpack = message.GetMethod("Unpack", Instance, new[] { typeof(byte[]) });
            FieldInfo? messageTypeField = message.GetField("messageType", Instance);
            FieldInfo? priorityField = message.GetField("messagePriority", Instance);
            FieldInfo? requestIdField = message.GetField("requestID", Instance);
            FieldInfo? bodyField = message.GetField("messageBody", Instance);
            if (generateKey is null || claim is null || release is null || encrypt is null || decrypt is null || pack is null || unpack is null || messageTypeField is null || priorityField is null || requestIdField is null || bodyField is null || !HasParameterNames(encrypt, "src", "useCompression", "shiftReturn") || !HasParameterNames(decrypt, "msgType", "messageData", "cryptoKey", "decryptedData"))
            {
                return false;
            }

            byte[]? cryptoKey = generateKey.Invoke(null, new object[] { connectionKeySeed }) as byte[];
            if (cryptoKey is null || cryptoKey.Length != 32)
            {
                return false;
            }
            adapter = new NativeDiagnosticVendorAdapter(cryptoKey, nativeMessageType, pack, unpack, messageTypeField, priorityField, requestIdField, bodyField, claim, release, encrypt, decrypt);
            return true;
        }
        catch (Exception exception) when (exception is TargetInvocationException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Creates an uncompressed encrypted DEBUG packet that retains the native request id.</summary>
    public bool TryCreateWirePacket(NetworkMessageType type, int requestId, string? debugMessage, out byte[] wire)
    {
        wire = Array.Empty<byte>();
        if (string.IsNullOrWhiteSpace(debugMessage))
        {
            return false;
        }
        try
        {
            byte[]? body = new DebugRequestData { debugMessage = debugMessage }.Serialize();
            if (body is null || body.Length > NativeDiagnosticProtocol.MaximumEnvelopeBytes)
            {
                return false;
            }
            object message = Activator.CreateInstance(_pack.DeclaringType!)!;
            _messageTypeField.SetValue(message, Enum.ToObject(_messageType, (byte)type));
            _priorityField.SetValue(message, Enum.ToObject(_priorityField.FieldType, 0));
            _requestIdField.SetValue(message, requestId);
            _bodyField.SetValue(message, body);
            byte[]? packed = _pack.Invoke(message, new object[] { requestId }) as byte[];
            return packed is not null && packed.Length <= MaximumWireBytes && TryEncrypt(type, packed, out wire);
        }
        catch (Exception exception) when (exception is TargetInvocationException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Decrypts only safe uncompressed native packets and exposes the DEBUG body after unpacking.</summary>
    public bool TryReadWirePacket(byte[]? wire, NetworkMessageType expectedType, out int requestId, out string? debugMessage)
    {
        requestId = 0;
        debugMessage = null;
        if (wire is null || wire.Length < MinimumWireBytes || wire.Length > MaximumWireBytes || wire[0] != (byte)expectedType || wire[1] != EncryptedMarker || (wire[CompressionFlagOffset] & 0x80) != 0)
        {
            return false;
        }
        try
        {
            byte[] messageData = new byte[wire.Length - 1];
            Array.Copy(wire, 1, messageData, 0, messageData.Length);
            object?[] args = { Enum.ToObject(_messageType, (byte)expectedType), messageData, _cryptoKey, null };
            if (_decrypt.Invoke(null, args) is not bool success || !success || args[3] is not byte[] packed || packed.Length < 10 || packed.Length > MaximumWireBytes)
            {
                return false;
            }
            object message = Activator.CreateInstance(_unpack.DeclaringType!)!;
            if (_unpack.Invoke(message, new object[] { packed }) is not bool unpacked || !unpacked || _messageTypeField.GetValue(message) is not Enum actualType || Convert.ToByte(actualType) != (byte)expectedType || _requestIdField.GetValue(message) is not int id || _bodyField.GetValue(message) is not byte[] body || body.Length > NativeDiagnosticProtocol.MaximumEnvelopeBytes)
            {
                return false;
            }
            var debug = new DebugRequestData();
            if (!debug.Deserialize(body) || string.IsNullOrWhiteSpace(debug.debugMessage) || !debug.Serialize().AsSpan().SequenceEqual(body))
            {
                return false;
            }
            requestId = id;
            debugMessage = debug.debugMessage;
            return true;
        }
        catch (Exception exception) when (exception is TargetInvocationException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private bool TryEncrypt(NetworkMessageType type, byte[] packed, out byte[] wire)
    {
        wire = Array.Empty<byte>();
        object? encryptor = null;
        try
        {
            encryptor = _claim.Invoke(null, new object[] { _cryptoKey });
            byte[]? encrypted = encryptor is null ? null : _encrypt.Invoke(encryptor, new object[] { packed, false, 2u }) as byte[];
            if (encrypted is null || encrypted.Length < MinimumWireBytes || encrypted.Length > MaximumWireBytes)
            {
                return false;
            }
            encrypted[0] = (byte)type;
            encrypted[1] = EncryptedMarker;
            wire = encrypted;
            return true;
        }
        catch (Exception exception) when (exception is TargetInvocationException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
        finally
        {
            if (encryptor is not null)
            {
                try { _release.Invoke(null, new[] { encryptor }); } catch (TargetInvocationException) { }
            }
        }
    }

    private static bool HasParameterNames(MethodInfo method, params string[] expected)
    {
        ParameterInfo[] actual = method.GetParameters();
        if (actual.Length != expected.Length)
        {
            return false;
        }
        for (int index = 0; index < expected.Length; index++)
        {
            if (!string.Equals(actual[index].Name, expected[index], StringComparison.Ordinal))
            {
                return false;
            }
        }
        return true;
    }

}
