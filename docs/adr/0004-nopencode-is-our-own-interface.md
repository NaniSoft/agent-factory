# NOpenCode is our own interface, not a package we depend on

We define the orchestrator-to-OpenCode boundary ourselves, and call it `NOpenCode`, so
the design's vocabulary stays true. We do **not** take a dependency on the existing
NuGet package of that name.

A package called `NOpenCode` does exist — `ylvict/NOpenCode`, MIT, `dotnet add package
NOpenCode` — and the published docs treat it as the boundary: named four times, given a
glossary entry, and credited with the design's most important property, that the
orchestrator is testable against a fake. It has **one star**, one maintainer, and was
created in June 2026. We rejected it as a dependency: every property the design depends
on would hang off a package that one person can break, and the failure would arrive as a
runtime break mid-build rather than a compile error.

For v1 the interface is implemented by driving the OpenCode CLI non-interactively inside
the worker container and capturing what it produces. That makes the round boundary a
process boundary and requires no network service inside the container.

# Consequences

The docs' claim that "worker containers need no inbound ports at all" becomes true
rather than aspirational. It was false the moment we adopted the package, because that
package's stated design is to auto-start `opencode serve` — an HTTP server on port 4096
inside the container, which the host would have to reach. We had not reconciled that
with the no-inbound-ports commitment until we read the package.

If the CLI mode proves inadequate, the fallback is a thin client we write over
`opencode serve`'s HTTP API. That is a few hundred lines against an API we now know
exists, and it sits behind the same interface, so swapping it in is not a design change.

A future reader who goes looking for a `PackageReference` to NOpenCode will not find one.
That is deliberate, and this ADR is why.
