using System.Text.Json.Nodes;

namespace ClaudeGraft.Core.Pool;

/// The title of a conversation the relay carried, by the session id it saw:
/// `X-Claude-Code-Session-Id` is the `cliSessionId` a record names.
public static class SessionTitles
{
    public static string? Find(string? profile, string session)
    {
        if (profile == null) return null;
        var store = Path.Combine(profile, "claude-code-sessions");
        if (!Directory.Exists(store)) return null;
        try
        {
            foreach (var file in Directory.EnumerateFiles(store, "local_*.json", SearchOption.AllDirectories)
                         .OrderByDescending(Fs.Modified).Take(200))
            {
                if (Fs.ReadShared(file) is not { } data || !Bytes.Contains(data, session)) continue;
                if (JsonNode.Parse(data) is not JsonObject record) continue;
                var ids = new[] { "cliSessionId", "lastAssistantCliSessionId" }.Select(k => record[k]?.ToString());
                if (!ids.Contains(session)) continue;
                return record["title"]?.ToString();
            }
        }
        catch { }
        return null;
    }
}
