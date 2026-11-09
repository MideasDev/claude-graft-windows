namespace ClaudeGraft.Core.Pool;

/// The relay's memory: who its members are, what each account has left,
/// and which account each conversation was last answered by. Logins live
/// here and nowhere else, so a relay that restarts knows nothing until it
/// is configured again and passes every request through untouched meanwhile:
/// unshared, never broken.
public sealed class PoolState
{
    public sealed record Attempt(string Member, string? Token);

    sealed class Runtime
    {
        public int Served;
        public DateTime? LastServed, LastSeen;
        public string? RefusedToken;
    }

    sealed class Conversation
    {
        public string From = "";
        public string? ServedBy;
        public DateTime LastAt;
        public int Requests;
        public string? Model;
        public TimeSpan CacheLifetime = PoolPolicy.ShortCache;
        public List<PoolPass> PassedOver = [];
        public bool Threaded;
        public DateTime? StartedAt;
    }

    public static readonly TimeSpan ConversationMemory = TimeSpan.FromHours(24);

    readonly object gate = new();
    PoolConfiguration? configuration;
    Dictionary<string, string> memberByDigest = [];
    Dictionary<string, string> lendingDigest = [];
    Dictionary<string, Runtime> runtime = [];
    Dictionary<string, PoolLimits> limits = [];
    readonly Dictionary<string, Conversation> conversations = [];
    readonly List<PoolRouting> recent = [];
    int active;

    string AccountKey(string id) => configuration?.Members.FirstOrDefault(m => m.Id == id)?.Account ?? id;

    public void Configure(PoolConfiguration next, DateTime now)
    {
        lock (gate)
        {
            configuration = next;
            memberByDigest = [];
            lendingDigest = [];
            var kept = new Dictionary<string, Runtime>();
            foreach (var member in next.Members)
            {
                foreach (var digest in member.Identify) memberByDigest.TryAdd(digest, member.Id);
                var state = runtime.GetValueOrDefault(member.Id) ?? new Runtime();
                if (member.Token != null)
                {
                    var digest = UsagePool.Digest(member.Token);
                    lendingDigest[member.Id] = digest;
                    memberByDigest.TryAdd(digest, member.Id);
                    // A login handed over again is given a fresh chance.
                    if (state.RefusedToken != digest) state.RefusedToken = null;
                }
                var key = member.Account ?? member.Id;
                if (member.Limits is { } pushed && pushed.Observed > (limits.GetValueOrDefault(key)?.Observed ?? DateTime.MinValue))
                {
                    var refused = limits.GetValueOrDefault(key)?.RefusedUntil;
                    limits[key] = refused is { } r && r > now ? pushed with { RefusedUntil = r } : pushed;
                }
                kept[member.Id] = state;
            }
            runtime = kept;
            var named = next.Members.Select(m => m.Account ?? m.Id).ToHashSet();
            limits = limits.Where(p => named.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value);
        }
    }

    public string? MemberFor(string bearer, DateTime now)
    {
        lock (gate)
        {
            if (!memberByDigest.TryGetValue(UsagePool.Digest(bearer), out var id)) return null;
            (runtime.TryGetValue(id, out var r) ? r : runtime[id] = new Runtime()).LastSeen = now;
            return id;
        }
    }

    /// The sender and the accounts it names, one account counted once: under
    /// the first of its names that can lend.
    public static List<PoolMember> Candidates(PoolMember sender, IReadOnlyList<PoolMember> members, Func<PoolMember, bool>? canLend = null)
    {
        canLend ??= m => m.Token != null;
        var output = new List<PoolMember> { sender };
        var place = new Dictionary<string, int> { [sender.Account ?? sender.Id] = 0 };
        foreach (var id in sender.BorrowsFrom ?? [])
        {
            if (id == sender.Id || members.FirstOrDefault(m => m.Id == id) is not { } lender) continue;
            var account = lender.Account ?? lender.Id;
            if (place.TryGetValue(account, out var index))
            {
                if (index > 0 && !canLend(output[index]) && canLend(lender)) output[index] = lender;
            }
            else
            {
                place[account] = output.Count;
                output.Add(lender);
            }
        }
        return output;
    }

    /// The order in which accounts are tried for one request.
    public List<Attempt> Plan(string own, string? session, bool pinned, bool bound, DateTime now)
    {
        lock (gate)
        {
            List<Attempt> asSent = [new(own, null)];
            if (configuration is not { Enabled: true } || pinned ||
                configuration.Members.FirstOrDefault(m => m.Id == own) is not { } sender) return asSent;

            bool CanLend(PoolMember m) => m.Token != null && runtime.GetValueOrDefault(m.Id)?.RefusedToken != lendingDigest.GetValueOrDefault(m.Id);
            var candidates = Candidates(sender, configuration.Members, CanLend);
            if (candidates.Count < 2) return asSent;
            var accounts = candidates.Select(m =>
                new PoolPolicy.Account(m.Id, limits.GetValueOrDefault(m.Account ?? m.Id), m.Id == own || CanLend(m)));

            var prefer = new List<string>();
            if (session != null)
            {
                conversations.TryGetValue(session, out var held);
                if (configuration.Assignments?.GetValueOrDefault(session) is { } chosen)
                {
                    var account = configuration.Members.FirstOrDefault(m => m.Id == chosen) is { } c ? c.Account ?? c.Id : chosen;
                    if (candidates.FirstOrDefault(m => (m.Account ?? m.Id) == account) is { } counted) prefer.Add(counted.Id);
                }
                if (held?.ServedBy is { } last && (bound || now - held.LastAt < held.CacheLifetime)) prefer.Add(last);
            }
            if (bound && prefer.Count == 0) prefer.Add(own);
            return PoolPolicy.Order(own, accounts, prefer, now)
                .Select(id => id == own ? new Attempt(own, null) : new Attempt(id, candidates.First(m => m.Id == id).Token))
                .ToList();
        }
    }

    public void Observe(string member, IReadOnlyList<(string, string)> headers, DateTime now)
    {
        if (PoolLimits.Parse(headers, now) is not { } parsed) return;
        lock (gate) limits[AccountKey(member)] = parsed;
    }

    public void Apply(PoolPolicy.Outcome outcome, string member, DateTime now)
    {
        lock (gate)
        {
            switch (outcome.Kind)
            {
                case PoolPolicy.OutcomeKind.Exhausted:
                    var key = AccountKey(member);
                    var known = limits.GetValueOrDefault(key) ?? new PoolLimits(0, null, 0, null, null, now);
                    limits[key] = known with { RefusedUntil = outcome.Until };
                    break;
                case PoolPolicy.OutcomeKind.LoginRefused:
                    (runtime.TryGetValue(member, out var r) ? r : runtime[member] = new Runtime()).RefusedToken = lendingDigest.GetValueOrDefault(member);
                    break;
            }
        }
    }

    /// Only an answer to a request carrying a thread says where a threaded
    /// conversation is: a title asked under the same session id would
    /// otherwise move it to where it will be refused.
    public void Served(string member, string own, string? session, int status, List<PoolPass> passedOver, string? model,
                       TimeSpan cacheLifetime, bool threaded, bool starts, DateTime now)
    {
        lock (gate)
        {
            var state = runtime.TryGetValue(member, out var r) ? r : runtime[member] = new Runtime();
            state.Served++;
            state.LastServed = now;
            if (session != null)
            {
                if (!conversations.TryGetValue(session, out var held))
                    conversations[session] = held = new Conversation { LastAt = now, StartedAt = starts ? now : null };
                held.From = own;
                held.Requests++;
                if (threaded || !held.Threaded)
                {
                    held.LastAt = now;
                    held.Model = model ?? held.Model;
                    held.PassedOver = passedOver;
                    if (status is >= 200 and < 300)
                    {
                        held.ServedBy = member;
                        held.CacheLifetime = cacheLifetime;
                        held.Threaded |= threaded;
                    }
                }
                if (conversations.Count > 256)
                    foreach (var stale in conversations.Where(p => now - p.Value.LastAt >= ConversationMemory).Select(p => p.Key).ToList())
                        conversations.Remove(stale);
            }
            recent.Add(new PoolRouting(now, own, member, status, passedOver, model, session));
            if (recent.Count > 40) recent.RemoveRange(0, recent.Count - 40);
        }
    }

    public void Began() => Interlocked.Increment(ref active);
    public void Ended() => Interlocked.Decrement(ref active);
    public int InFlight => Volatile.Read(ref active);

    public PoolStatus Status(string binary, DateTime started, DateTime now)
    {
        lock (gate)
        {
            var members = (configuration?.Members ?? []).Select(m =>
            {
                var state = runtime.GetValueOrDefault(m.Id);
                return new PoolMemberStatus(m.Id, limits.GetValueOrDefault(m.Account ?? m.Id), state?.Served ?? 0,
                                            state?.LastServed, state?.LastSeen, state?.RefusedToken != null);
            }).ToList();
            var sessions = conversations.Where(p => now - p.Value.LastAt < ConversationMemory)
                .OrderByDescending(p => p.Value.LastAt).Take(64)
                .Select(p => new PoolSession(p.Key, p.Value.From, p.Value.ServedBy, p.Value.LastAt, p.Value.Requests, p.Value.Model,
                    p.Value.ServedBy == null ? null : p.Value.LastAt + p.Value.CacheLifetime, p.Value.PassedOver, p.Value.StartedAt))
                .ToList();
            return new PoolStatus(binary, Environment.ProcessId, started, configuration?.Enabled ?? false,
                                  configuration != null, members, [.. recent], sessions, InFlight);
        }
    }
}
