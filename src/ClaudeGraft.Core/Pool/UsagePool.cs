using System.Security.Cryptography;
using System.Text;

namespace ClaudeGraft.Core.Pool;

/// Shared usage: one Claude's Claude Code drawing on other accounts'
/// allowance. The code calls it the usage pool. Ported from the macOS
/// original; the reasoning behind every rule is in its
/// `notes/shared-usage.md`, and what differs on Windows in `docs/PORTING.md`.
public static class UsagePool
{
    /// Fixed, because a session reads it once as it starts, and a port that
    /// moved would leave every session already running pointed at nothing.
    public const int Port = 47823;
    public const string Host = "api.anthropic.com";
    public const string LendingScope = "user:sessions:claude_code";
    public const string InferenceScope = "user:inference";

    public static string Digest(string token) => Digest(Encoding.UTF8.GetBytes(token));
    public static string Digest(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));
}
