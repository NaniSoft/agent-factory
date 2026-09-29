namespace AgentFactory.Tests.Boundary;

using Microsoft.Extensions.Logging;

/// <summary>
/// Every log record the running factory wrote, kept as it was written: the level, the
/// category, the message, the exception, and the <em>structured state</em> — which is the
/// message's own named parameters plus every scope that was open when it was written.
/// </summary>
/// <remarks>
/// <para>
/// This exists because the claim "what happened to this work item is answerable without
/// reading prose" can only be checked against the records a sink would receive, and a sink
/// receives state, not sentences. A test that asserted on message text would be asserting
/// that a string is a string: it would pass for a record whose identity could only be read
/// by a person, and it would pass for a record whose identity was in the prose and not in
/// the fields. So the structured form is what is captured, and what the assertions read.
/// </para>
/// <para>
/// It reads the host's own scope provider — the one the console and JSON sinks read — rather
/// than keeping a stack of its own, which is the whole of what makes the records faithful:
/// a scope the factory's loggers opened is pushed by the <c>ILoggerFactory</c> and never by a
/// provider, so there is nowhere else to look. Reading it means what a test asserts on is
/// what a real sink would have written — including a record written deep inside a round, on a
/// task the loop started in one step and landed in another.
/// </para>
/// </remarks>
public sealed class RecordedLog : ILoggerProvider, ISupportExternalScope
{
    private readonly List<Record> _records = [];
    private IExternalScopeProvider? _scopes;

    /// <summary>
    /// The host's own scope provider, handed to this provider because it asks for one. This
    /// is how every real scope-carrying sink works, and it is the only way to see a scope the
    /// factory's loggers opened.
    /// </summary>
    /// <remarks>
    /// Which is why <see cref="FactoryHost.RecordingAsync"/> clears the host's other
    /// providers: a scope provider is only handed out when there is exactly one provider, so
    /// with the console left in place this would see every record and no scope. The provider
    /// is execution-flow-local inside, which is load-bearing rather than incidental — a round
    /// outlives the step that started it, so the scope opened around the loop's call has to
    /// travel into the round's own continuations for the round's records to carry the work
    /// item, and a stack held in a field would make the whole claim false under concurrency.
    ///
    /// A logger handed out by <see cref="For{T}"/> has no host, so it keeps a stack of its
    /// own; that is the one case where the chain is this recorder's rather than the host's.
    /// </remarks>
    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

    /// <summary>Every record, in the order it was written.</summary>
    public IReadOnlyList<Record> Records
    {
        get
        {
            lock (_records)
            {
                return _records.ToList();
            }
        }
    }

    /// <summary>Every record at or above a level, which is most of what a test wants.</summary>
    public IReadOnlyList<Record> AtLeast(LogLevel level) =>
        [.. Records.Where(record => record.Level >= level)];

    /// <summary>
    /// The records belonging to one work item, read off the scope rather than off the
    /// message. This is the query the whole of the traceability argument rests on, and it is
    /// one method here so that no test has to hand-roll a filter which might match on prose
    /// by accident.
    /// </summary>
    public IReadOnlyList<Record> About(Guid workItemId) =>
        [.. Records.Where(record => record.Guids(AgentFactory.Observability.WorkItemScope.WorkItemIdKey)
            .Contains(workItemId))];

    /// <summary>
    /// Everything any sink would have received, as one blob, for a "this string is not in
    /// there" check. The message, the state and the exception's own message are all included,
    /// because a credential that reached a named parameter is exactly as leaked as one that
    /// reached a sentence.
    /// </summary>
    public string Everything => string.Join('\n', Records.Select(record => record.Everything));

    public ILogger CreateLogger(string categoryName) => new Category(this, categoryName);

    /// <summary>
    /// A logger for one component, for a test that constructs a component by hand rather
    /// than through the host. The same capture, without the provider registration — and it
    /// keeps its own scope stack in that case, because there is no host to hand it one.
    /// </summary>
    public ILogger For(string categoryName) => CreateLogger(categoryName);

    /// <summary>The same, under the category a component's own logger would carry.</summary>
    public ILogger<T> For<T>() => new Typed<T>(CreateLogger(typeof(T).FullName ?? typeof(T).Name));

    public void Dispose()
    {
    }

    private sealed class Typed<T>(ILogger inner) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => inner.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            inner.Log(logLevel, eventId, state, exception, formatter);
    }

    private void Add(Record record)
    {
        lock (_records)
        {
            _records.Add(record);
        }
    }

    /// <summary>
    /// The scopes this recorder pushed itself, for a logger used without a host. An
    /// execution-flow-local, so a scope travels into the continuations of a call made inside
    /// it — which is exactly what the loop does when it asks for a round and walks away.
    /// </summary>
    private readonly AsyncLocal<IReadOnlyList<object?>?> _ownScopes = new();

    private sealed class Category(RecordedLog log, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            if (log._scopes is { } host)
            {
                return host.Push(state);
            }

            var opened = log._ownScopes.Value ?? [];
            log._ownScopes.Value = [.. opened, state];
            return new Closer(() => log._ownScopes.Value = opened);
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            log.Add(new Record(
                logLevel,
                category,
                formatter(state, exception),
                exception,
                Fields(state),
                log.Open()));
    }

    private sealed class Closer(Action onDispose) : IDisposable
    {
        private Action? _onDispose = onDispose;

        public void Dispose() => Interlocked.Exchange(ref _onDispose, null)?.Invoke();
    }

    /// <summary>
    /// The open scope chain, innermost first, as each scope's own field list. The callback
    /// is the generic one with a state of <c>object?</c> because that is the only way to ask
    /// for *every* scope rather than the ones of a type this recorder knows about — which is
    /// the whole point, since some of the scopes in a running factory are ASP.NET's and this
    /// recorder has never heard of them.
    /// </summary>
    private IReadOnlyList<IReadOnlyList<KeyValuePair<string, object?>>> Open()
    {
        var scopes = new List<IReadOnlyList<KeyValuePair<string, object?>>>();
        if (_scopes is { } host)
        {
            host.ForEachScope<object?>((scope, _) => scopes.Add(ScopeOf(scope)), state: null);
        }
        else
        {
            foreach (var scope in _ownScopes.Value ?? [])
            {
                scopes.Add(ScopeOf(scope));
            }
        }

        scopes.Reverse();
        return scopes;
    }

    /// <summary>
    /// One scope, read as the bag of named fields it is, and nothing else. A scope that is
    /// not one contributes nothing rather than being guessed at — and that is not tidiness:
    /// ASP.NET Core pushes scopes of its own around every request, and a recorder that
    /// walked one after the request had ended would throw rather than record.
    /// </summary>
    private static IReadOnlyList<KeyValuePair<string, object?>> ScopeOf(object? scope)
    {
        if (scope is not IEnumerable<KeyValuePair<string, object?>> fields)
        {
            return [];
        }

        try
        {
            // Copied now, while whatever the scope describes is still alive. A scope is a
            // live reference rather than a snapshot — ASP.NET Core's own scopes describe the
            // request in flight — so a recorder that kept the reference and read it after the
            // request had ended would throw instead of recording, which is the one thing a
            // log provider must never do.
            return [.. fields];
        }
        catch (ObjectDisposedException)
        {
            return [];
        }
    }

    /// <summary>
    /// How a record's state is read out of the <c>ILogger</c> state object. Every sink reads
    /// the named values off the state by reflection, and so does this, because the keys are
    /// what make a record queryable — and reading them any other way would be reading
    /// something a real sink does not see.
    /// </summary>
    private static IReadOnlyList<KeyValuePair<string, object?>> Fields<TState>(TState state)
    {
        // Copied rather than kept, for the same reason the scope is: a sink writes the
        // record when it is handed it, and anything held afterwards is a reference to
        // something that may no longer exist.
        if (state is IReadOnlyList<KeyValuePair<string, object?>> pairs)
        {
            return [.. pairs];
        }

        var field = typeof(TState).GetField("Values") ?? typeof(TState).GetField("_values");
        return field?.GetValue(state) is IEnumerable<KeyValuePair<string, object?>> inner
            ? [.. inner]
            : [];
    }

    /// <summary>One record, as a sink received it.</summary>
    /// <param name="Level">The level it was written at.</param>
    /// <param name="Category">The component that wrote it, by logger category.</param>
    /// <param name="Message">The formatted message, for a human.</param>
    /// <param name="Exception">The exception attached to it, or null.</param>
    /// <param name="Parameters">The message's own named parameters.</param>
    /// <param name="Scopes">The scope chain, outermost first, as it was when the record was written.</param>
    public sealed record Record(
        LogLevel Level,
        string Category,
        string Message,
        Exception? Exception,
        IReadOnlyList<KeyValuePair<string, object?>> Parameters,
        IReadOnlyList<IReadOnlyList<KeyValuePair<string, object?>>> Scopes)
    {
        /// <summary>
        /// Every field on the record: the scopes first and the message's own parameters
        /// after, so the message wins a collision — the order a sink merges them in, and the
        /// reason the scope keys are prefixed so the two cannot collide at all.
        /// </summary>
        public IReadOnlyList<KeyValuePair<string, object?>> Fields
        {
            get
            {
                var fields = new List<KeyValuePair<string, object?>>();
                foreach (var scope in Scopes)
                {
                    fields.AddRange(scope);
                }

                fields.AddRange(Parameters);
                return fields;
            }
        }

        /// <summary>The values a key carries anywhere on this record, in scope-then-message order.</summary>
        public IReadOnlyList<object?> Values(string key) =>
            [.. Fields.Where(pair => string.Equals(pair.Key, key, StringComparison.Ordinal))
                .Select(pair => pair.Value)];

        /// <summary>The value of a key, and a failure rather than a wrong answer if it is absent.</summary>
        public object? Field(string key) => Values(key).FirstOrDefault()
            ?? throw new InvalidOperationException(
                $"the record has no {key}: it was written by {Category} at {Level} saying {Message}");

        /// <summary>Every value of a key that is a <see cref="Guid"/>, for filtering a scope by identity.</summary>
        public IReadOnlyList<Guid> Guids(string key) => [.. Values(key).OfType<Guid>()];

        /// <summary>The record as a sink would write it: message, state, and the exception's own message.</summary>
        public string Everything =>
            $"{Message} {string.Join(' ', Fields.Select(pair => $"{pair.Key}={pair.Value}"))} "
                + $"{Exception?.Message} {Exception?.InnerException?.Message}";
    }
}
