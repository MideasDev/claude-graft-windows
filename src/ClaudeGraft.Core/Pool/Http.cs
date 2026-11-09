using System.Text;
using System.Text.Json.Nodes;

namespace ClaudeGraft.Core.Pool;

public abstract record HttpBody
{
    public sealed record None : HttpBody;
    public sealed record Length(long Count) : HttpBody;
    public sealed record Chunked : HttpBody;
    public sealed record UntilClose : HttpBody;
    public static readonly HttpBody NoBody = new None();
}

/// A request or status line and its fields, exactly as they came, so what
/// cannot change accounts can be sent on byte for byte.
public sealed class HttpHead
{
    public string[] Line { get; }
    public List<(string Name, string Value)> Fields { get; }

    HttpHead(string[] line, List<(string, string)> fields) { Line = line; Fields = fields; }

    public static HttpHead? Parse(byte[] data)
    {
        var text = Encoding.Latin1.GetString(data);
        var lines = text.Split("\r\n").ToList();
        while (lines.Count > 0 && lines[^1] == "") lines.RemoveAt(lines.Count - 1);
        if (lines.Count == 0) return null;
        var line = lines[0].Split(' ', 3);
        if (line.Length < 2 || line[0].Length == 0 || line[1].Length == 0) return null;
        var fields = new List<(string, string)>();
        foreach (var raw in lines.Skip(1))
        {
            if (raw.StartsWith(' ') || raw.StartsWith('\t'))
            {
                if (fields.Count == 0) return null;
                fields[^1] = (fields[^1].Item1, fields[^1].Item2 + " " + raw.Trim());
                continue;
            }
            var colon = raw.IndexOf(':');
            if (colon <= 0) return null;
            var name = raw[..colon];
            if (name.Contains(' ') || name.Contains('\t')) return null;
            fields.Add((name, raw[(colon + 1)..].Trim()));
        }
        return new HttpHead(line, fields);
    }

    public string Method => Line[0];
    public string Target => Line[1];
    public int? Status => int.TryParse(Line[1], out var s) ? s : null;
    public string Version => Line[0].StartsWith("HTTP/") ? Line[0] : Line.Length > 2 ? Line[2] : "";

    public string? Value(string name) => Fields.FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;

    public List<string> Tokens(string name) => Fields
        .Where(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        .SelectMany(f => f.Value.Split(','))
        .Select(t => t.Trim().ToLowerInvariant())
        .Where(t => t.Length > 0).ToList();

    public bool AsksToUpgrade => Value("upgrade") != null && Tokens("connection").Contains("upgrade");

    public bool KeepsAlive
    {
        get
        {
            var connection = Tokens("connection");
            return Version == "HTTP/1.0" ? connection.Contains("keep-alive") : !connection.Contains("close");
        }
    }

    /// Null for a request whose framing cannot be trusted.
    public HttpBody? RequestBody
    {
        get
        {
            if (Value("transfer-encoding") != null)
                return Tokens("transfer-encoding").LastOrDefault() == "chunked" ? new HttpBody.Chunked() : null;
            var lengths = Fields.Where(f => f.Name.Equals("content-length", StringComparison.OrdinalIgnoreCase))
                .Select(f => f.Value).Distinct().ToList();
            if (lengths.Count > 1) return null;
            if (lengths.Count == 0) return HttpBody.NoBody;
            if (!long.TryParse(lengths[0], out var n) || n < 0) return null;
            return n == 0 ? HttpBody.NoBody : new HttpBody.Length(n);
        }
    }

    public HttpBody ResponseBody(string method)
    {
        if (Status is not { } status) return new HttpBody.UntilClose();
        if (method == "HEAD" || status is >= 100 and < 200 || status is 204 or 304) return HttpBody.NoBody;
        if (Value("transfer-encoding") != null)
            return Tokens("transfer-encoding").LastOrDefault() == "chunked" ? new HttpBody.Chunked() : new HttpBody.UntilClose();
        if (long.TryParse(Value("content-length"), out var n) && n >= 0) return n == 0 ? HttpBody.NoBody : new HttpBody.Length(n);
        return new HttpBody.UntilClose();
    }
}

/// Walks a chunked body as it arrives, so a stream can be forwarded piece
/// by piece: a client may send its next piece only after reading the answer
/// to the last, and holding the whole body first would deadlock it.
public sealed class ChunkedFraming
{
    enum State { Size, Data, DataEnd, Trailer, Done }
    State state = State.Size;
    long remaining;
    readonly List<byte> line = [];

    public bool IsDone => state == State.Done;

    /// How many bytes of `input` belong to the body; the payload, when asked
    /// for, is appended to `payload`.
    public int Consume(ReadOnlySpan<byte> input, List<byte>? payload)
    {
        var index = 0;
        while (index < input.Length && state != State.Done)
        {
            if (state == State.Data)
            {
                var count = (int)Math.Min(remaining, input.Length - index);
                payload?.AddRange(input.Slice(index, count));
                index += count;
                remaining -= count;
                if (remaining == 0) state = State.DataEnd;
                continue;
            }
            var feed = input[index..].IndexOf((byte)'\n');
            if (feed < 0)
            {
                line.AddRange(input[index..]);
                index = input.Length;
                if (line.Count > 16384) throw new InvalidDataException("chunk line too long");
                break;
            }
            line.AddRange(input.Slice(index, feed));
            index += feed + 1;
            if (line.Count > 0 && line[^1] == '\r') line.RemoveAt(line.Count - 1);
            var text = Encoding.ASCII.GetString(line.ToArray());
            line.Clear();
            switch (state)
            {
                case State.Size:
                    var digits = text.Split(';', 2)[0].Trim();
                    if (!long.TryParse(digits, System.Globalization.NumberStyles.HexNumber, null, out var size) || size < 0)
                        throw new InvalidDataException("bad chunk size");
                    if (size == 0) state = State.Trailer;
                    else { remaining = size; state = State.Data; }
                    break;
                case State.DataEnd:
                    if (text.Length != 0) throw new InvalidDataException("bad chunk end");
                    state = State.Size;
                    break;
                default:
                    if (text.Length == 0) state = State.Done;
                    break;
            }
        }
        return index;
    }
}

public static class HttpMessage
{
    static readonly HashSet<string> HopFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "connection", "keep-alive", "proxy-connection", "proxy-authorization", "proxy-authenticate",
        "te", "trailer", "transfer-encoding", "upgrade", "expect", "content-length",
    };

    /// A model request written again: the same fields less this hop's, the
    /// lent login in place of the sender's where one is lent, and a length.
    /// `plainAnswer` asks for an uncompressed answer, so a refusal of a
    /// moved thread can be read.
    public static byte[] Request(HttpHead head, byte[] body, string? authorization, bool plainAnswer = false)
    {
        var named = head.Tokens("connection").ToHashSet();
        var text = new StringBuilder($"{head.Method} {head.Target} HTTP/1.1\r\n");
        var sawHost = false;
        foreach (var (name, value) in head.Fields)
        {
            var lower = name.ToLowerInvariant();
            if (HopFields.Contains(lower) || named.Contains(lower)) continue;
            if (lower == "authorization" && authorization != null) continue;
            if (lower == "accept-encoding" && plainAnswer) continue;
            if (lower == "host") sawHost = true;
            text.Append(name).Append(": ").Append(value).Append("\r\n");
        }
        if (!sawHost) text.Append("Host: ").Append(UsagePool.Host).Append("\r\n");
        if (authorization != null) text.Append("Authorization: ").Append(authorization).Append("\r\n");
        if (plainAnswer) text.Append("Accept-Encoding: identity\r\n");
        if (body.Length > 0 || head.Method is "POST" or "PUT" or "PATCH") text.Append("Content-Length: ").Append(body.Length).Append("\r\n");
        text.Append("\r\n");
        return [.. Encoding.Latin1.GetBytes(text.ToString()), .. body];
    }

    public static byte[] Failure(int status, string reason, string message)
    {
        var body = Encoding.UTF8.GetBytes(new JsonObject
        {
            ["type"] = "error",
            ["error"] = new JsonObject { ["type"] = "api_error", ["message"] = message },
        }.ToJsonString());
        var head = $"HTTP/1.1 {status} {reason}\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
        return [.. Encoding.ASCII.GetBytes(head), .. body];
    }
}

/// A connection with a read buffer: heads are read off it, bodies taken as
/// framed, and whatever is left stays for the next request.
public sealed class Wire : IDisposable
{
    readonly Stream stream;
    byte[] buffer = new byte[64 * 1024];
    int start, end;
    public long Received { get; private set; }

    public Wire(Stream stream) { this.stream = stream; }

    public int Buffered => end - start;
    public ReadOnlySpan<byte> Pending => buffer.AsSpan(start, end - start);

    public async Task<bool> FillAsync(CancellationToken cancel = default)
    {
        if (start > 0 && start == end) start = end = 0;
        if (end == buffer.Length)
        {
            if (start > 0) { Buffer.BlockCopy(buffer, start, buffer, 0, end - start); end -= start; start = 0; }
            else Array.Resize(ref buffer, buffer.Length * 2);
        }
        int read;
        try { read = await stream.ReadAsync(buffer.AsMemory(end), cancel).ConfigureAwait(false); }
        catch (IOException) { return false; }
        catch (ObjectDisposedException) { return false; }
        if (read <= 0) return false;
        end += read;
        Received += read;
        return true;
    }

    public byte[] Take(int count)
    {
        var taken = buffer.AsSpan(start, count).ToArray();
        start += count;
        return taken;
    }

    public byte[] TakeAll() => Take(Buffered);

    public async Task<(HttpHead Head, byte[] Raw)?> ReadHeadAsync(int limit)
    {
        while (true)
        {
            var at = Pending.IndexOf("\r\n\r\n"u8);
            if (at >= 0)
            {
                var raw = Take(at + 4);
                return HttpHead.Parse(raw) is { } head ? (head, raw) : throw new InvalidDataException("unreadable head");
            }
            if (Buffered > limit) throw new InvalidDataException("head too long");
            if (!await FillAsync().ConfigureAwait(false))
            {
                if (Buffered == 0) return null;
                throw new EndOfStreamException();
            }
        }
    }

    public async Task<byte[]> ReadBodyAsync(HttpBody framing, long limit)
    {
        switch (framing)
        {
            case HttpBody.Length { Count: var total }:
                if (total > limit) throw new RelayTooLarge();
                while (Buffered < total)
                    if (!await FillAsync().ConfigureAwait(false)) throw new EndOfStreamException();
                return Take((int)total);
            case HttpBody.Chunked:
                var chunked = new ChunkedFraming();
                var payload = new List<byte>();
                while (true)
                {
                    if (Buffered > 0) start += chunked.Consume(Pending, payload);
                    if (chunked.IsDone) return [.. payload];
                    if (payload.Count > limit) throw new RelayTooLarge();
                    if (!await FillAsync().ConfigureAwait(false)) throw new EndOfStreamException();
                }
            case HttpBody.UntilClose:
                while (await FillAsync().ConfigureAwait(false))
                    if (Buffered > limit) throw new RelayTooLarge();
                return TakeAll();
            default:
                return [];
        }
    }

    public async Task SendAsync(byte[] data)
    {
        await stream.WriteAsync(data).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
    }

    public Stream Stream => stream;

    public void Dispose()
    {
        try { stream.Dispose(); } catch { }
    }
}

public sealed class RelayTooLarge : Exception;
