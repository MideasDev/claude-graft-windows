using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeGraft.Core.Pool;

/// The two lines that reach Claude Code: `HTTPS_PROXY` and
/// `NODE_EXTRA_CA_CERTS`, the only variables Desktop lets through to the
/// Code process (it overwrites the address and the token). They live in the
/// profile's `ccd-environment-config.json`, whose `envVars` is the base64 of
/// Electron's safe storage over a JSON object, exactly as the Code tab's own
/// environment editor saves it.
///
/// Written only into a closed Claude: a running one decrypts the field once
/// and saves the whole of it back from that editor, so a write landing while
/// it is open would never be read and would then be written over. On
/// Windows the safe-storage key opens without a dialog, so the launcher
/// itself settles the lines on its way to opening a Claude, which on macOS
/// needed a journal of values Graft had sealed beforehand.
public static class PoolEnvironment
{
    public const string FileName = "ccd-environment-config.json";
    public const string Field = "envVars";
    public const string ScopedField = "scopedEnvVars";
    public const string ProxyName = "HTTPS_PROXY";
    public const string CertificateName = "NODE_EXTRA_CA_CERTS";

    public static string ProxyValue => $"http://127.0.0.1:{UsagePool.Port}";
    public static string CertificateValue => PoolCertificates.AuthorityFile;
    public static Dictionary<string, string> Lines => new() { [ProxyName] = ProxyValue, [CertificateName] = CertificateValue };

    public enum State { Off, On, Waiting, Leaving, Conflict, Unreadable }

    public sealed record Result(State State, string? Conflict = null)
    {
        public bool HoldsLines => State is State.On or State.Leaving;
    }

    static string FilePath(string profile) => Path.Combine(profile, FileName);

    /// Null while Claude's store is part way through a write (a journal
    /// beside the file), or when the file is there and will not parse.
    static JsonObject? Outer(string profile)
    {
        var path = FilePath(profile);
        if (File.Exists(path + ".journal")) return null;
        if (!File.Exists(path)) return new JsonObject();
        var data = Fs.ReadShared(path);
        if (data == null) return null;
        try { return JsonNode.Parse(data) as JsonObject; } catch { return null; }
    }

    static JsonNode? Unseal(string sealedText, byte[] key)
    {
        try
        {
            var plain = SafeStorage.Decrypt(Convert.FromBase64String(sealedText), key);
            return plain == null ? null : JsonNode.Parse(plain);
        }
        catch { return null; }
    }

    static string Seal(JsonNode value, byte[] key) =>
        Convert.ToBase64String(SafeStorage.Encrypt(Encoding.UTF8.GetBytes(value.ToJsonString()), key));

    /// A proxy or certificate a person set themselves is theirs, matched
    /// without regard to case since a lower-case proxy variable is read too.
    public static string? ConflictIn(IEnumerable<KeyValuePair<string, string>> variables)
    {
        foreach (var (name, value) in variables)
            foreach (var (ours, wanted) in Lines)
                if (name.Equals(ours, StringComparison.OrdinalIgnoreCase) && value != wanted) return ours;
        return null;
    }

    static string? ConflictInNested(JsonNode? node) => node switch
    {
        JsonObject o when o.All(p => p.Value is JsonValue) =>
            ConflictIn(o.Select(p => new KeyValuePair<string, string>(p.Key, p.Value?.ToString() ?? ""))),
        JsonObject o => o.Select(p => ConflictInNested(p.Value)).FirstOrDefault(c => c != null),
        _ => null,
    };

    /// Takes exact copies of the lines out of the folder-scoped values,
    /// which Claude lays over the profile-wide ones: a copy there would
    /// outlive the lines' removal and keep Claude Code pointed at a relay
    /// that may have stopped.
    static (JsonNode? Kept, bool Found) WithoutLines(JsonNode? node)
    {
        if (node is not JsonObject map) return (node?.DeepClone(), false);
        var kept = new JsonObject();
        var found = false;
        foreach (var (name, value) in map)
        {
            if (value is JsonValue v && v.TryGetValue<string>(out var text) &&
                Lines.Any(l => name.Equals(l.Key, StringComparison.OrdinalIgnoreCase) && text == l.Value))
            {
                found = true;
                continue;
            }
            var inner = WithoutLines(value);
            found |= inner.Found;
            kept[name] = inner.Kept;
        }
        return (kept, found);
    }

    static bool Write(string profile, JsonObject outer)
    {
        var data = Encoding.UTF8.GetBytes(outer.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return Fs.WriteAtomic(FilePath(profile), data);
    }

    /// Reads, and with `apply` writes, a closed profile's lines. `wanted` is
    /// whether this Claude borrows anything; the lines go in only when the
    /// relay is up, because a Claude pointed at a proxy that is not there has
    /// no Claude Code at all.
    public static Result Settle(string profile, bool wanted, bool relayReady, bool apply)
    {
        var outer = Outer(profile);
        if (outer == null) return new(State.Unreadable);
        var field = outer[Field];
        var scoped = outer[ScopedField];
        var nothingThere = field == null && scoped == null;
        if (nothingThere && !(wanted && relayReady && apply)) return new(wanted ? State.Waiting : State.Off);

        var key = SafeStorage.MasterKey(profile);
        if (key == null) return new(nothingThere && !wanted ? State.Off : State.Unreadable);

        var variables = new Dictionary<string, string>();
        if (field is JsonValue fv && fv.TryGetValue<string>(out var sealedText))
        {
            if (Unseal(sealedText, key) is not JsonObject o) return new(State.Unreadable);
            foreach (var (name, value) in o)
            {
                if (value is not JsonValue sv || !sv.TryGetValue<string>(out var text)) return new(State.Unreadable);
                variables[name] = text;
            }
        }
        else if (field != null) return new(State.Unreadable);

        if (ConflictIn(variables) is { } conflict) return new(wanted ? State.Conflict : State.Off, conflict);

        JsonNode? scopedObject = null;
        if (scoped is JsonValue scv && scv.TryGetValue<string>(out var scopedText))
        {
            scopedObject = Unseal(scopedText, key);
            if (scopedObject == null) return new(State.Unreadable);
            if (ConflictInNested(scopedObject) is { } nested) return new(wanted ? State.Conflict : State.Off, nested);
        }

        var holding = Lines.All(l => variables.GetValueOrDefault(l.Key) == l.Value);
        var (scopedKept, scopedCopies) = WithoutLines(scopedObject);

        if (apply)
        {
            var changed = false;
            if (scopedCopies && scopedKept != null)
            {
                outer[ScopedField] = Seal(scopedKept, key);
                changed = true;
            }
            var target = wanted && relayReady;
            if (target && !holding)
            {
                foreach (var (name, value) in Lines) variables[name] = value;
                holding = true;
                changed = true;
            }
            else if (!target && holding)
            {
                foreach (var name in Lines.Keys) variables.Remove(name);
                holding = false;
                changed = true;
            }
            if (changed)
            {
                if (variables.Count == 0) outer.Remove(Field);
                else outer[Field] = Seal(new JsonObject(variables.OrderBy(p => p.Key, StringComparer.Ordinal)
                    .Select(p => KeyValuePair.Create(p.Key, (JsonNode?)p.Value))), key);
                var ok = Write(profile, outer);
                Diagnostics.Note("pool.environment-written", $"{Path.GetFileName(profile)} lines={holding} ok={ok}");
                if (!ok) return new(State.Unreadable);
            }
            scopedCopies = false;
        }

        var holds = holding || scopedCopies;
        return new(wanted ? (holds ? State.On : State.Waiting) : (holds ? State.Leaving : State.Off));
    }
}
