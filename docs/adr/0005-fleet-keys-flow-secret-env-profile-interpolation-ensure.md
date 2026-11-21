# Fleet keys flow Secret → pod env → profile interpolation → per-run ensure

In the containerized deployment, fleet credentials live only in the
Kubernetes Secret `agent-factory-keys` (`ZEN_CREDENTIAL` — the whole zen
OAuth envelope, `GO_API_KEY`, `GH_TOKEN`). The Secret mounts as pod env;
agent profiles reference those env vars (`{env:ZEN_CREDENTIAL}` /
`{env:GO_API_KEY}`), interpolation happens at injection time
(`ProfileCredential`), and `EnsureCredentialAsync` PUTs the shape-sniffed
payload into the opencode serve at run start, restarting the serve once
when it didn't boot with that credential — because opencode auth is
boot-time. The opencode credential store inside the container stays
disposable and holds nothing authoritative: the Secret is the single
credential truth, so rotation is `kubectl apply` on the Secret plus one
ensure restart on the next run. Key material never enters the image, the
config document, or the PVC (decided across tickets 037/042, deployed by
040, 2026-10-31).

## Considered Options

- **Seeding a native opencode credential store on the PVC**: rejected —
  it copies key material out of the Secret onto a durable volume that
  outlives pods, escapes the Secret's rotation story, and goes stale the
  moment the Secret changes (nothing re-syncs a persisted store, and
  auth is boot-time, so a stale store silently serves dead credentials
  until someone deletes it by hand).
