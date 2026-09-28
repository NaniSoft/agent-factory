# A round's output leaves the container as a file, not a stream or a mount

Structured results cross the container boundary as a single file the host lifts out with
`docker cp` after the round ends. Live progress crosses as `docker logs`. The container
publishes no port, and the host mounts nothing into it.

The design specifies the result's *content* — files changed, commands run, test outcomes —
but never says how anything crosses the boundary at all. This is the mechanism every other
property rests on: the board's review view, the "nothing ends in silence" guarantee, and
the commit retrieval that ADR-0006 already requires.

We rejected streaming `docker exec` output and parsing it, because parsing model output as
text is exactly the fragility the docs name when they warn about "a results payload that
parses but is missing the field the reviewer needs". We rejected a bind-mounted volume,
because it drags host path ownership and permission mapping into the design — which the
docs already identify as a host-provisioning concern — and on Windows with Docker Desktop
that mapping is a recurring source of breakage.

# Consequences

`docker cp` needs no mount, so the worker container has no host paths in it at all. That
keeps ADR-0006's property intact in the strongest form: a container that cannot read the
host's filesystem, cannot reach the network, and holds no write credential.

The result file and the log have different jobs and different failure modes, so they travel
separately. A round whose result file is malformed or absent still has a log, and the board
can say what happened rather than showing nothing.

Because the host already lifts a commit out of the container for ADR-0006, retrieval is one
mechanism serving two purposes rather than two mechanisms to keep in step.
