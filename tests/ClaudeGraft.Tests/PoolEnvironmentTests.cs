using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using ClaudeGraft.Core;
using ClaudeGraft.Core.Pool;

/// The lines in a stand-in profile sealed the way Claude seals its own:
/// a DPAPI-wrapped key in Local State, and the environment field as base64
/// of safe storage over JSON.
static class PoolEnvironmentTests
{
    static byte[] SignedProfile(string dir)
    {
        Directory.CreateDirectory(dir);
        var key = RandomNumberGenerator.GetBytes(32);
        File.WriteAllText(Path.Combine(dir, "Local State"), new JsonObject
        {
            ["os_crypt"] = new JsonObject { ["encrypted_key"] = Convert.ToBase64String([.. "DPAPI"u8, .. Dpapi.Protect(key)]) },
        }.ToJsonString());
        return key;
    }

    static string Seal(JsonNode value, byte[] key) =>
        Convert.ToBase64String(SafeStorage.Encrypt(Encoding.UTF8.GetBytes(value.ToJsonString()), key));

    static JsonObject? Variables(string dir, byte[] key, string field = PoolEnvironment.Field)
    {
        var outer = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, PoolEnvironment.FileName)))!;
        if (outer[field] is not JsonValue v) return null;
        return JsonNode.Parse(SafeStorage.Decrypt(Convert.FromBase64String(v.GetValue<string>()), key)!) as JsonObject;
    }

    static void Write(string dir, JsonObject outer) => File.WriteAllText(Path.Combine(dir, PoolEnvironment.FileName), outer.ToJsonString());

    public static void Run(Action<bool, string> check, string sandbox)
    {
        var dir = Path.Combine(sandbox, "pool-env");
        var key = SignedProfile(dir);

        var r = PoolEnvironment.Settle(dir, wanted: true, relayReady: true, apply: true);
        check(r.State == PoolEnvironment.State.On, "a borrowing Claude, closed, with the relay up, gets the lines");
        var vars = Variables(dir, key)!;
        check(vars[PoolEnvironment.ProxyName]?.GetValue<string>() == "http://127.0.0.1:47823", "the proxy line points at the relay's fixed port");
        check(vars[PoolEnvironment.CertificateName]?.GetValue<string>() == PoolCertificates.AuthorityFile, "the certificate line names the relay's authority");

        Write(dir, new JsonObject
        {
            [PoolEnvironment.Field] = Seal(new JsonObject { ["MY_VAR"] = "kept", [PoolEnvironment.ProxyName] = PoolEnvironment.ProxyValue,
                                                            [PoolEnvironment.CertificateName] = PoolEnvironment.CertificateValue }, key),
            ["tmpDirOverride"] = "x",
        });
        r = PoolEnvironment.Settle(dir, wanted: true, relayReady: false, apply: true);
        check(r.State == PoolEnvironment.State.Waiting, "with no relay to reach the lines come out, so Claude Code still works");
        vars = Variables(dir, key)!;
        check(vars["MY_VAR"]?.GetValue<string>() == "kept" && vars[PoolEnvironment.ProxyName] == null,
              "taking the lines out keeps every variable a person set");
        check(JsonNode.Parse(File.ReadAllText(Path.Combine(dir, PoolEnvironment.FileName)))!["tmpDirOverride"]?.GetValue<string>() == "x",
              "and every other setting in the file");

        PoolEnvironment.Settle(dir, wanted: true, relayReady: true, apply: true);
        r = PoolEnvironment.Settle(dir, wanted: false, relayReady: true, apply: true);
        check(r.State == PoolEnvironment.State.Off && Variables(dir, key)!.ContainsKey("MY_VAR") &&
              !Variables(dir, key)!.ContainsKey(PoolEnvironment.CertificateName),
              "a Claude that stops borrowing loses the lines and keeps its own variables");

        Write(dir, new JsonObject { [PoolEnvironment.Field] = Seal(new JsonObject { ["https_proxy"] = "http://corporate:8080" }, key) });
        var before = File.ReadAllText(Path.Combine(dir, PoolEnvironment.FileName));
        r = PoolEnvironment.Settle(dir, wanted: true, relayReady: true, apply: true);
        check(r.State == PoolEnvironment.State.Conflict && r.Conflict == PoolEnvironment.ProxyName,
              "a proxy a person set, in any case, is reported");
        check(File.ReadAllText(Path.Combine(dir, PoolEnvironment.FileName)) == before, "and left exactly as it was");

        var scopedValue = new JsonObject { [":machine:C:\\proj"] = new JsonObject { [PoolEnvironment.ProxyName] = PoolEnvironment.ProxyValue, ["OTHER"] = "y" } };
        Write(dir, new JsonObject { [PoolEnvironment.ScopedField] = Seal(scopedValue, key) });
        check(PoolEnvironment.Settle(dir, wanted: false, relayReady: false, apply: false).HoldsLines,
              "a copy of the lines in a folder's own environment counts as holding them");
        PoolEnvironment.Settle(dir, wanted: false, relayReady: false, apply: true);
        var scoped = Variables(dir, key, PoolEnvironment.ScopedField)!;
        check(scoped[":machine:C:\\proj"]?[PoolEnvironment.ProxyName] == null && scoped[":machine:C:\\proj"]?["OTHER"]?.GetValue<string>() == "y",
              "copies of the lines come out of folder environments, and nothing else does");

        File.WriteAllText(Path.Combine(dir, PoolEnvironment.FileName + ".journal"), "");
        before = File.ReadAllText(Path.Combine(dir, PoolEnvironment.FileName));
        check(PoolEnvironment.Settle(dir, wanted: true, relayReady: true, apply: true).State == PoolEnvironment.State.Unreadable &&
              File.ReadAllText(Path.Combine(dir, PoolEnvironment.FileName)) == before,
              "nothing is decided while Claude's store is part way through a write");
        File.Delete(Path.Combine(dir, PoolEnvironment.FileName + ".journal"));

        File.WriteAllText(Path.Combine(dir, PoolEnvironment.FileName), "{ half");
        check(PoolEnvironment.Settle(dir, wanted: true, relayReady: true, apply: true).State == PoolEnvironment.State.Unreadable &&
              File.ReadAllText(Path.Combine(dir, PoolEnvironment.FileName)) == "{ half",
              "an unreadable environment file is never written over");

        var bare = Path.Combine(sandbox, "pool-bare");
        Directory.CreateDirectory(bare);
        check(PoolEnvironment.Settle(bare, wanted: false, relayReady: false, apply: true).State == PoolEnvironment.State.Off &&
              !File.Exists(Path.Combine(bare, PoolEnvironment.FileName)),
              "a Claude that borrows nothing has nothing written into it");
    }

    /// The members the relay is told about, read from stand-in profiles.
    public static void RunMembers(Action<bool, string> check, Store store, string mainProfile, DateTime now)
    {
        var key = SignedProfile(mainProfile);
        // From the real clock: the members are read against it, not the suite's fixed date.
        var expires = (DateTime.UtcNow.AddHours(4) - DateTime.UnixEpoch).TotalMilliseconds;
        var cache = new JsonObject
        {
            ["c:o:https://api.anthropic.com:user:inference user:sessions:claude_code"] = new JsonObject { ["token"] = "code-token", ["expiresAt"] = expires },
            ["c:o:https://api.anthropic.com:user:inference"] = new JsonObject { ["token"] = "inference-token", ["expiresAt"] = expires + 99999 },
            ["c:o:https://api.anthropic.com:user:profile"] = new JsonObject { ["token"] = "profile-token", ["expiresAt"] = expires },
        };
        File.WriteAllText(Path.Combine(mainProfile, "config.json"), new JsonObject
        {
            ["lastKnownAccountUuid"] = "acc-main",
            ["oauth:tokenCacheV2"] = Convert.ToBase64String(SafeStorage.Encrypt(Encoding.UTF8.GetBytes(cache.ToJsonString()), key)),
        }.ToJsonString());

        var settings = new SharingSettings { Lending = { ["main"] = ["account:x"] } };
        settings.Accounts.Add(new PoolAccount
        {
            Id = "account:x", AccountUuid = "acc-x", Email = "lender@example.com", Expires = now.AddDays(300),
            Sealed = Convert.ToBase64String(Dpapi.Protect(Encoding.UTF8.GetBytes("signed-in-token"))),
        });
        settings.Accounts.Add(new PoolAccount { Id = "account:old", Expires = now.AddDays(-1), Sealed = "" });
        var config = SharedUsage.Members(store, settings);
        var main = config.Members.Single(m => m.Id == "main");
        check(main.Token == "code-token", "a Claude lends the login Desktop gives Claude Code, ahead of a longer inference one");
        check(main.Identify.Count == 3 && main.Identify.Contains(UsagePool.Digest("profile-token")),
              "every token a profile holds identifies its requests");
        check(main.Account == "acc-main" && main.BorrowsFrom!.SequenceEqual(["account:x"]), "a Claude's account and lenders are passed on");
        check(config.Members.Single(m => m.Id == "account:x").Token == "signed-in-token", "an account signed in here lends its own sealed login");
        check(!config.Members.Any(m => m.Id == "account:old"), "an account whose login has expired lends nothing");
        File.Delete(Path.Combine(mainProfile, "config.json"));
        File.Delete(Path.Combine(mainProfile, "Local State"));
    }
}
