# Rootless Docker is a deployment property, not a development one

The factory requires a rootless Docker daemon **where it runs for real**. Local development
on Docker Desktop is an accepted deviation with named compensating controls. `DESIGN.md`
must stop implying that rootless holds everywhere.

The design makes rootless a named security consideration and argues it hard: "a rootful
Docker daemon is effectively root on the host… the factory deliberately opens that path
hundreds of times a day, on work it did not write." That argument is sound and we are not
discarding it. But Docker Desktop on Windows runs a Linux VM, reports
`SecurityOptions: seccomp, cgroupns` with no rootless marker, and there is no rootless
Docker Desktop to switch to. The development machine cannot satisfy the requirement, and
claiming otherwise would be the exact dishonesty the Nexus docs explicitly guard against:
"no invented features" and "live / in-development / designed stay separated everywhere".

# Consequences

Docker Desktop's VM is a real isolation boundary from the host even though it is not
rootless in the POSIX sense, so the compensating controls carry the development posture.
They are listed here so that the deviation is bounded rather than open-ended:

- no credential that can write to a remote exists inside a worker container (ADR-0006)
- no host path is mounted into a worker container (ADR-0010)
- one fresh container per round, removed when it ends, with nothing surviving it
- credentials are per project and scoped to the narrowest set that works
- the board binds to loopback only

Any of these going away on a development machine means the machine is no longer a safe place
to run the factory, regardless of what the daemon reports.

The requirement itself is not weakened for deployment. A deployment on a rootful daemon is
out of policy, and the compensating controls are not a substitute for it — they are what
makes the development deviation tolerable.
