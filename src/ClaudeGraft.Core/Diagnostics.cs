namespace ClaudeGraft.Core;

/// `diagnostics.log`, read before guessing. It never holds a token or the
/// contents of a chat: events and folder names only.
public static class Diagnostics
{
    static readonly object Gate = new();
    public static string Who { get; set; } = "app";

    public static void Note(string evt, string? detail = null)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Paths.GraftHome);
                var log = Paths.LogFile;
                var info = new FileInfo(log);
                // A log nobody trims grows forever on a machine nobody watches.
                if (info.Exists && info.Length > 1_000_000)
                    File.Move(log, log + ".1", overwrite: true);
                File.AppendAllText(log, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{Who}] {evt}" +
                                        (detail == null ? "" : $" {detail}") + Environment.NewLine);
            }
        }
        catch { }
    }
}
