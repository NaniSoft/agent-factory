# The Board is a Prism app served statically at the root over a loopback JSON API

The human surface is authored with Prism (`@nanisoft/prism-ui`, pinned from npm at
an exact version) rather than hand-written, and it is served by the same .NET
process that runs the factory. The app is a flat Next 16 static export
(`output: 'export'` with `trailingSlash: true`, no base path); its `out/` directory
is published under `wwwroot` beside the entry assembly and served at the root by the
same static-file middleware the board's old stylesheet used, with default files so
each route's directory index (`projects/index.html`, `credentials/index.html`,
`work-items/index.html`) answers a direct load. It reads the factory over a JSON API
at `/api/*`, on the same loopback binding as the board.

This replaces the hand-written Razor board, and the cutover is complete (#51): the
Razor pages, their handlers, the board's `board.css` and the Razor registration are
gone, and the app renders what the pages rendered from the same records. The board
has always been one ASP.NET Core endpoint in the one process (ADR-0003); this keeps
that shape and changes the client that draws it.

The design system is a published dependency and never a fork: the app composes
Prism items, writes no Prism utility classes and no stylesheet of Prism's own, and
runs the consumer gate kit (`prism-gates`), whose laws live in the package. The app
wears the lavender pack, dark by default.

# Consequences

Serving is unchanged in trust terms. The process binds loopback (ADR-0005,
ADR-0012), so the static export and the JSON beside it are reachable only from the
machine that already runs the factory, and there is no new authentication surface
because there is no new reachability. The JSON API reads; the writes it will grow
mirror existing page handlers and add no capability.

The app has no server of its own. It is static files plus a client that fetches the
factory's API, so the same process serves the surface and the data, and a restart
of the factory is a restart of both. Serving the app and the API from one origin
under one process is what makes "the board is one endpoint" still true of the
address a reader uses.

Because the export is published beside the entry assembly, the app has to be built
before the .NET publish for the root to have content; an empty `admin/out` is an
empty copy, and a checkout that has not run `pnpm build` serves no app rather than
failing the .NET build. The project copies the export with a wildcard and leaves the
ordering to whoever builds the repository.
