using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace ClaudeGraft.Core;

/// A plan's live limits from the endpoint Claude Code itself uses: exact
/// reset times and current values, even for a profile that is closed. A
/// token goes to `api.anthropic.com` and nowhere else.
public static class UsageApi
{
    public static readonly Uri Endpoint = new("https://api.anthropic.com/api/oauth/usage");

    public sealed class Refused(HttpStatusCode code, TimeSpan? retryAfter) : Exception(Describe(code))
    {
        public HttpStatusCode Code { get; } = code;
        public TimeSpan? RetryAfter { get; } = retryAfter;
    }

    static string Describe(HttpStatusCode code) => code switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
            S.T("That login was refused. Open the profile once so Claude can renew it."),
        HttpStatusCode.TooManyRequests =>
            S.T("Anthropic is rate-limiting usage checks. The last figures stay on screen."),
        _ => S.F("The usage service answered {0}.", (int)code),
    };

    static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(10) };

    public static async Task<UsageFigure> FetchAsync(string token, CancellationToken cancel = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Add("anthropic-beta", "oauth-2025-04-20");
        using var response = await Client.SendAsync(request, cancel).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK)
            throw new Refused(response.StatusCode, response.Headers.RetryAfter?.Delta);
        var body = await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false);
        return Reading(JsonDocument.Parse(body).RootElement, DateTime.UtcNow)
            ?? throw new InvalidDataException(S.T("The usage service sent something unexpected."));
    }

    /// Kept apart from the request so the suite can feed it answers. An
    /// account with no five-hour limit is sent `five_hour: null` or no key,
    /// while a limited one always gets the window, at zero when idle; a
    /// window that is there and cannot be read is a broken answer, refused.
    public static UsageFigure? Reading(JsonElement body, DateTime now)
    {
        if (body.ValueKind != JsonValueKind.Object) return null;
        var sent = body.TryGetProperty("five_hour", out var fh) && fh.ValueKind != JsonValueKind.Null;
        var session = sent ? Window(fh) : null;
        if (sent && session == null) return null;
        var weekly = body.TryGetProperty("seven_day", out var sd) ? Window(sd) : null;
        if (session == null && weekly == null) return null;
        var plan = body.TryGetProperty("subscription_type", out var p) && p.ValueKind == JsonValueKind.String
            ? CultureInfo.InvariantCulture.TextInfo.ToTitleCase(p.GetString()!) : null;
        return new UsageFigure(session?.Used, weekly?.Used ?? 0, session?.Resets, weekly?.Resets,
                               now, UsageFigure.Live, plan);
    }

    static (int Used, DateTime? Resets)? Window(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) return null;
        if (!value.TryGetProperty("utilization", out var u) || !u.TryGetDouble(out var used)) return null;
        DateTime? resets = null;
        if (value.TryGetProperty("resets_at", out var r))
        {
            if (r.ValueKind == JsonValueKind.String &&
                DateTime.TryParse(r.GetString(), CultureInfo.InvariantCulture,
                                  DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at))
                resets = at;
            else if (r.ValueKind == JsonValueKind.Number && r.TryGetDouble(out var seconds))
                resets = DateTime.UnixEpoch.AddSeconds(seconds);
        }
        return ((int)Math.Round(used), resets);
    }
}
