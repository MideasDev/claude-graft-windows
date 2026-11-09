using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeGraft.Core;

/// `claude_desktop_config.json` belongs to each profile. Copying adds only
/// the MCP servers the other profile lacks; a definition already there, even
/// a different one, is that profile's own and stays as it is.
public static class DesktopServers
{
    public const string FileName = "claude_desktop_config.json";

    public static int CopyMissing(string fromProfile, string intoProfile)
    {
        if (Paths.Same(fromProfile, intoProfile)) return 0;
        var source = Read(Path.Combine(fromProfile, FileName));
        if (source?["mcpServers"] is not JsonObject offered || offered.Count == 0) return 0;

        var targetPath = Path.Combine(intoProfile, FileName);
        JsonObject target;
        if (File.Exists(targetPath))
        {
            // An unreadable file is not an empty one: writing over it would
            // lose whatever that profile had.
            target = Read(targetPath) ?? throw new InvalidDataException(
                S.F("{0} could not be read, so it was left as it is.", targetPath));
        }
        else target = new JsonObject();

        if (target["mcpServers"] is not JsonObject servers)
        {
            servers = new JsonObject();
            target["mcpServers"] = servers;
        }
        var added = 0;
        foreach (var (name, definition) in offered)
        {
            if (servers.ContainsKey(name)) continue;
            servers[name] = definition?.DeepClone();
            added++;
        }
        if (added == 0) return 0;
        Directory.CreateDirectory(intoProfile);
        var temp = targetPath + ".graft-tmp";
        File.WriteAllText(temp, target.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, targetPath, overwrite: true);
        return added;
    }

    static JsonObject? Read(string path)
    {
        try { return File.Exists(path) ? JsonNode.Parse(SafeStorage.ReadShared(path)) as JsonObject : null; }
        catch { return null; }
    }
}
