using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeGraft.Core;

/// Claude's login, borrowed rather than taken. On Windows, Electron's safe
/// storage is Chromium's: a 32-byte key in `Local State` under
/// `os_crypt.encrypted_key`, sealed with DPAPI for this Windows user and
/// prefixed `DPAPI`, and values sealed with AES-256-GCM as
/// `v10 | 12-byte nonce | ciphertext | 16-byte tag`.
///
/// Only the access token is ever decrypted, never the refresh token:
/// Anthropic rotates those, and using one would sign Claude out. Nothing is
/// ever written back.
public static class SafeStorage
{
    public static byte[]? MasterKey(string profileDir)
    {
        try
        {
            var localState = Path.Combine(profileDir, "Local State");
            if (!File.Exists(localState)) return null;
            var root = JsonNode.Parse(ReadShared(localState));
            var encoded = root?["os_crypt"]?["encrypted_key"]?.GetValue<string>();
            if (encoded == null) return null;
            var sealedKey = Convert.FromBase64String(encoded);
            var prefix = "DPAPI"u8;
            if (sealedKey.Length <= prefix.Length || !sealedKey.AsSpan(0, prefix.Length).SequenceEqual(prefix)) return null;
            return Dpapi.Unprotect(sealedKey[prefix.Length..]);
        }
        catch (Exception e)
        {
            Diagnostics.Note("credentials.key-unreadable", $"{Path.GetFileName(profileDir)} {e.GetType().Name}");
            return null;
        }
    }

    public static byte[]? Decrypt(byte[] blob, byte[] key)
    {
        if (blob.Length < 3 + 12 + 16) return null;
        var version = Encoding.ASCII.GetString(blob, 0, 3);
        if (version != "v10" && version != "v11")
        {
            // Before Chromium kept a key of its own, a value was DPAPI alone.
            try { return Dpapi.Unprotect(blob); } catch { return null; }
        }
        var nonce = blob.AsSpan(3, 12);
        var body = blob.AsSpan(15, blob.Length - 15 - 16);
        var tag = blob.AsSpan(blob.Length - 16, 16);
        var plain = new byte[body.Length];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(nonce, body, tag, plain);
            return plain;
        }
        catch (CryptographicException) { return null; }
    }

    /// The other direction, so the suite can seal a stand-in login exactly
    /// the way Claude would.
    internal static byte[] Encrypt(byte[] plain, byte[] key)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var body = new byte[plain.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(key, 16)) aes.Encrypt(nonce, plain, body, tag);
        return [.. "v10"u8, .. nonce, .. body, .. tag];
    }

    internal static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

internal static partial class Dpapi
{
    [StructLayout(LayoutKind.Sequential)]
    struct Blob { public int Size; public IntPtr Data; }

    [LibraryImport("crypt32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy,
                                                   IntPtr reserved, IntPtr prompt, int flags, ref Blob output);

    [LibraryImport("crypt32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptProtectData(ref Blob input, IntPtr description, IntPtr entropy,
                                                 IntPtr reserved, IntPtr prompt, int flags, ref Blob output);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr LocalFree(IntPtr memory);

    const int UiForbidden = 0x1;

    public static byte[] Unprotect(byte[] data) => Run(data, protect: false);
    public static byte[] Protect(byte[] data) => Run(data, protect: true);

    static byte[] Run(byte[] data, bool protect)
    {
        var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            var input = new Blob { Size = data.Length, Data = handle.AddrOfPinnedObject() };
            var output = new Blob();
            var ok = protect
                ? CryptProtectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, ref output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, ref output);
            if (!ok) throw new CryptographicException(Marshal.GetLastPInvokeError());
            try
            {
                var result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, output.Size);
                return result;
            }
            finally { LocalFree(output.Data); }
        }
        finally { handle.Free(); }
    }
}

public sealed record AccessToken(string Value, DateTime Expires, string? Plan, IReadOnlyList<string> Scopes)
{
    public bool IsCurrent(DateTime now) => Expires > now.AddMinutes(2);

    /// An entry whose scopes could not be read from its key is not ruled
    /// out: the key's shape is Claude's to change, and the service refuses
    /// the request anyway if the token really is not enough.
    public bool Covers(string scope) => Scopes.Count == 0 || Scopes.Contains(scope);
}

public static class Credentials
{
    public const string UsageScope = "user:profile";
    static readonly string[] CacheKeys = ["oauth:tokenCacheV2", "oauth:tokenCache"];

    public enum Failure { None, NotSignedIn, NoKey, Unreadable, Expired }

    /// The freshest current token that may read usage, or why there is none.
    public static (AccessToken? Token, Failure Why) UsageToken(string profileDir, DateTime now)
    {
        var (all, why) = Tokens(profileDir);
        if (all == null) return (null, why);
        var best = all.Where(t => t.IsCurrent(now) && t.Covers(UsageScope)).MaxBy(t => t.Expires);
        return best == null ? (null, Failure.Expired) : (best, Failure.None);
    }

    public static (List<AccessToken>? Tokens, Failure Why) Tokens(string profileDir)
    {
        string? encoded = null;
        try
        {
            var config = Path.Combine(profileDir, "config.json");
            if (!File.Exists(config)) return (null, Failure.NotSignedIn);
            var root = JsonNode.Parse(SafeStorage.ReadShared(config)) as JsonObject;
            encoded = CacheKeys.Select(k => root?[k]?.GetValue<string>()).FirstOrDefault(v => v != null);
        }
        catch { return (null, Failure.Unreadable); }
        if (encoded == null) return (null, Failure.NotSignedIn);

        var key = SafeStorage.MasterKey(profileDir);
        if (key == null) return (null, Failure.NoKey);
        byte[] blob;
        try { blob = Convert.FromBase64String(encoded); } catch { return (null, Failure.Unreadable); }
        var plain = SafeStorage.Decrypt(blob, key);
        if (plain == null) return (null, Failure.Unreadable);
        try { return (ParseCache(plain), Failure.None); }
        catch { return (null, Failure.Unreadable); }
    }

    /// One entry per client, organization and scope set: the key carries the
    /// scopes, the entry carries the token.
    internal static List<AccessToken> ParseCache(byte[] plain)
    {
        var tokens = new List<AccessToken>();
        using var document = JsonDocument.Parse(plain);
        foreach (var entry in document.RootElement.EnumerateObject())
        {
            var value = entry.Value;
            if (value.ValueKind != JsonValueKind.Object) continue;
            if (!value.TryGetProperty("token", out var token) || token.ValueKind != JsonValueKind.String) continue;
            if (!value.TryGetProperty("expiresAt", out var expires) || !expires.TryGetDouble(out var ms)) continue;
            var plan = value.TryGetProperty("subscriptionType", out var p) && p.ValueKind == JsonValueKind.String
                ? p.GetString() : null;
            tokens.Add(new AccessToken(token.GetString()!, DateTime.UnixEpoch.AddMilliseconds(ms), plan,
                                       ScopesIn(entry.Name)));
        }
        return tokens;
    }

    /// Scopes follow the API host in the cache key, after a colon.
    internal static List<string> ScopesIn(string composite)
    {
        var marker = composite.IndexOf("https://", StringComparison.Ordinal);
        if (marker < 0) return [];
        var tail = composite[(marker + "https://".Length)..];
        var separator = tail.IndexOf(':');
        return separator < 0 ? [] : [.. tail[(separator + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries)];
    }
}
