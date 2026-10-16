// The factory's host: the resource graph, and nothing else (#63).
//
// This is the whole of `aspire`'s job in this repository. It composes the factory
// (`AgentFactory.Web`, launched as its own process) with the Temporal server the durable
// workflow lives in, and it is what carries telemetry — the Aspire dashboard is started by
// this host, and DCP points the factory's OTLP exporter at it. What the factory *is* stays
// in `FactoryApp.Create`, which this project only launches; see the Hosting section of
// `docs/architecture.md` for the line between the two.
//
// Two things about it are decisions rather than boilerplate, and both are argued at the
// line that makes them: which resources exist, and how the factory reaches Temporal.

var builder = DistributedApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------------
// Temporal — the durable workflow's server, and the resource the factory waits for.
// ---------------------------------------------------------------------------------
//
// It is a container rather than something already running, because the AppHost owning the
// server's lifetime is the whole point of "the AppHost orchestrates the Web app and
// Temporal": the graph starts it, stops it, and gives the factory its address.
//
// The image, tag, command and ports are `docker-compose.yml`'s temporal service, kept in
// step deliberately: that file is the *other* way to get a Temporal server — the way
// `dotnet test`'s durable tests and a bare `dotnet run --project src/AgentFactory.Web`
// use — and a second copy of a version that had drifted would be a server the tests and
// the AppHost disagreed about. The upstream image's entrypoint is the Cassandra-shaped
// auto-setup deployment script, so it is overridden to the server binary's `start-dev`,
// which is the documented single-binary dev server (SQLite, one process).
var temporal = builder.AddContainer("temporal", "temporalio/server", "1.28.0")
    .WithEntrypoint("/bin/sh")
    .WithArgs("-c", "exec /usr/local/bin/temporal server start-dev --ip 0.0.0.0 --port 7233 --ui-port 8233 --db-filename /tmp/temporal.db")
    // HTTP/2 on plaintext, which is what Temporal's gRPC port is; named `grpc` so the
    // reference below says which endpoint the factory is talking to rather than `0`.
    .WithEndpoint(targetPort: 7233, name: "grpc", scheme: "http")
    // Temporal's own UI, the same loopback surface docker-compose.yml exposes. DCP assigns
    // its host port, so an AppHost run and a compose run can coexist.
    .WithHttpEndpoint(targetPort: 8233, name: "ui");

// ---------------------------------------------------------------------------------
// The factory.
// ---------------------------------------------------------------------------------
//
// `Projects.AgentFactory_Web` is generated from this project's `ProjectReference`, so the
// path to the web project is a compile-time fact rather than a string a rename can strand.
//
// **The one environment variable is Temporal's address, and its form is load-bearing.**
// The factory already reads `Factory:Temporal:Endpoint` (double underscore is the
// configuration system's `:`, so this is exactly that key), and that value ends up in
// `Temporalio`'s `TargetHost`. `TargetHost` **rejects a URI**: `http://localhost:7233`
// throws `ArgumentException: TargetHost cannot have ://`, and `localhost:7233` — the form
// `docker-compose.yml` and the durable tests use — is the only shape it accepts. So the
// reference is the endpoint's `HostAndPort` and not the endpoint itself: handing over the
// endpoint's URL, which is the obvious wiring, is a factory that cannot start its worker.
// Measured rather than assumed, against this machine's Temporal; see `docs/research/aspire.md`
// §5.5 for the question and `docs/architecture.md` for the drawn graph.
var factory = builder.AddProject<Projects.AgentFactory_Web>("agent-factory")
    .WithEnvironment("Factory__Temporal__Endpoint", temporal.GetEndpoint("grpc").Property(EndpointProperty.HostAndPort))
    // **The board is not proxied, and this line is the whole of why the resource graph
    // works at all.** DCP puts a proxy in front of a project resource by default: the
    // declared host port belongs to the proxy, and the project itself is told to listen on
    // a random internal port. The factory does not obey that instruction — `FactoryApp.Create`
    // calls `UseUrls(options.BoardUrl)`, `UseUrls` beats `ASPNETCORE_URLS` (measured), and the
    // address it names is the pinned, tested `http://127.0.0.1:5000`. So the proxy takes 5000,
    // the web process cannot bind it and never serves, and
    // `http://127.0.0.1:5000/api/board` times out against a proxy with nothing behind it —
    // which is exactly what the first run of this AppHost did.
    //
    // Not proxying is also the *right* answer rather than merely the working one. ADR-0005
    // and ADR-0012 make loopback the authorisation: the machine that can open the board is
    // the machine allowed to decide what merges, and a hop in front of the listener is a
    // hop those documents did not account for. The project binds its own loopback port and
    // nothing sits in front of it; the dashboard links straight at it.
    .WithEndpoint("http", endpoint => endpoint.IsProxied = false)
    .WaitFor(temporal);

builder.Build().Run();
