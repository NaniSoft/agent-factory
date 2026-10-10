# The Board is a Prism app served statically at /admin over a loopback JSON API

The human surface is authored with Prism (`@nanisoft/prism-ui`, pinned from npm at
an exact version) rather than hand-written, and it is served by the same .NET
process that runs the factory. The app is a flat Next 16 static export
(`output: 'export'`, `basePath: '/admin'` with a matching `assetPrefix`); its
`out/` directory is published under `wwwroot/admin` beside the entry assembly and
served at `/admin` by the static-file middleware the board's stylesheet already
uses. It reads the factory over a JSON API at `/api/*`, on the same loopback
binding as the board.

This replaces the hand-written Razor board, at parity: the board is removed only
when the app renders what the pages rendered (the exhaustion ticket, #51), and
until then both exist and both read the same records. The board has always been one
ASP.NET Core endpoint in the one process (ADR-0003); this keeps that shape and
changes the client that draws it.

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
of the factory is a restart of both. A base path rather than a second origin keeps
the static assets and the API under one process, which is what makes "the board is
one endpoint" still true of the address a reader uses.

Because the export is published beside the entry assembly, the app has to be built
before the .NET publish for `/admin` to have content; an empty `admin/out` is an
empty copy and a board that renders without its app, not a build failure. The
project copies the export with a wildcard and leaves the ordering to whoever builds
the repository. The Razor board and its `board.css` keep working throughout, so a
checkout that has not built the app still serves a board.
