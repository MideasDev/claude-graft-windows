using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeGraft.Core;

/// One extra Claude: a name, the folder it keeps its profile in, its colour,
/// and the Windows shortcuts this app made for it (and may therefore rewrite
/// or remove; nothing else on disk is ever touched).
public sealed class Shortcut
{
    public string Folder { get; set; } = "";
    public string Name { get; set; } = "";
    public string Color { get; set; } = "#C2410C";
    public List<string> Links { get; set; } = [];
    /// Whose chats this Claude reads: null for its own (what a new Claude
    /// gets), `main` for Claude's own profile, or another Claude's folder.
    public string? Source { get; set; }
    public DateTime Created { get; set; } = DateTime.UtcNow;

    [JsonIgnore] public string ProfileDir => Paths.ProfileDir(Folder);
}

public sealed class StoreData
{
    public int Version { get; set; } = 1;
    public string MainColor { get; set; } = "#C2410C";
    public List<Shortcut> Shortcuts { get; set; } = [];
    /// Reading live figures means decrypting each profile's access token, so
    /// it is something a person turns on, never a default.
    public bool LiveUsage { get; set; }
}

public sealed class StoreUnreadableException(string path, Exception inner)
    : Exception(S.F("{0} could not be read, so it was left as it is.", path), inner);

/// `shortcuts.json`. An unreadable file is not an empty one: saving over it
/// would throw away every Claude it described, so a store that failed to load
/// refuses to save until the file is repaired or moved aside.
public sealed class Store
{
    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public StoreData Data { get; private set; } = new();
    public bool Broken { get; private set; }

    public static Store Load()
    {
        var store = new Store();
        var path = Paths.StoreFile;
        if (!File.Exists(path)) return store;
        try
        {
            var text = File.ReadAllText(path);
            store.Data = JsonSerializer.Deserialize<StoreData>(text, Options)
                ?? throw new InvalidDataException("empty document");
        }
        catch (Exception e)
        {
            store.Broken = true;
            Diagnostics.Note("store.unreadable", e.Message);
        }
        return store;
    }

    public void Save()
    {
        if (Broken) throw new StoreUnreadableException(Paths.StoreFile, new InvalidDataException());
        Directory.CreateDirectory(Paths.GraftHome);
        var temp = Paths.StoreFile + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(Data, Options));
        File.Move(temp, Paths.StoreFile, overwrite: true);
    }

    public Shortcut? Find(string folder) =>
        Data.Shortcuts.FirstOrDefault(s => string.Equals(s.Folder, folder, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<string> Folders => Data.Shortcuts.Select(s => s.Folder);

    /// A colour is handed out once: a new Claude takes the first one nobody
    /// wears, and nothing recolours a Claude that already exists.
    public static readonly string[] Palette =
    [
        "#C2410C", "#2563EB", "#16A34A", "#9333EA", "#DB2777",
        "#0891B2", "#CA8A04", "#4F46E5", "#DC2626", "#059669",
    ];

    public string NextColor()
    {
        var worn = new HashSet<string>(Data.Shortcuts.Select(s => s.Color).Append(Data.MainColor),
                                       StringComparer.OrdinalIgnoreCase);
        return Palette.FirstOrDefault(c => !worn.Contains(c)) ?? Palette[Data.Shortcuts.Count % Palette.Length];
    }

    public Shortcut Add(string name, string folder)
    {
        var problem = ProfileName.Validate(folder);
        if (problem != null) throw new ArgumentException(problem);
        if (Find(folder) != null) throw new ArgumentException(S.F("A Claude already uses the folder “{0}”.", folder));
        var shortcut = new Shortcut { Name = name.Trim(), Folder = folder.Trim(), Color = NextColor() };
        Directory.CreateDirectory(shortcut.ProfileDir);
        Data.Shortcuts.Add(shortcut);
        return shortcut;
    }
}
