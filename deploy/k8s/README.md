# Agent Factory on Kubernetes (docker-desktop)

One all-in-one pod: the .NET app plus opencode CLI, gh, and git inside
`agent-factory:dev`, executing the full fleet loop in-pod. Static Fleet
settings come from ConfigMap `agent-factory-config`, keys from Secret
`agent-factory-keys`, durable state lives on PVC `agent-factory-data` at
`/data`. (ADR 0005 covers the keys path.)

## Apply

```sh
kubectl apply -f deploy/k8s/namespace.yaml
kubectl apply -f deploy/k8s/configmap.yaml
kubectl apply -f deploy/k8s/pvc.yaml
kubectl apply -f deploy/k8s/deployment.yaml
```

The Secret `agent-factory-keys` (`GO_API_KEY`, `GH_TOKEN`) is
provisioned once by hand (ticket 037) and is not in these manifests — key
material never enters the repo.

## Seed the state volume (first deploy, or after a PVC wipe)

The PVC's config document is runtime state; it is seeded once from the
committed snapshot `seed-config.json` (Go-only: profile keyed to
`{env:GO_API_KEY}`, utility binding and the develop pin as proven live).
Re-run after a volume wipe; never re-run against a volume whose config
the UI has since edited — this overwrite is unconditional.

```sh
kubectl apply -f deploy/k8s/seed-pod.yaml
kubectl -n agent-factory wait --for=condition=Ready pod/agent-factory-seed
kubectl exec -i -n agent-factory agent-factory-seed -- sh -c \
  'cat > /data/config.json && chown 1654:0 /data/config.json' \
  < deploy/k8s/seed-config.json
kubectl delete -f deploy/k8s/seed-pod.yaml
```

Seed **before** first deploy of the Deployment (or scale it to zero
first) so the app never boots on an unseeded volume.

## Verify

```sh
kubectl -n agent-factory get pods
kubectl -n agent-factory port-forward deploy/agent-factory 8080:8080
# then: http://localhost:8080  (dashboard, profiles, pipelines, targets)
```

## Rebuild after an image change

The image is built locally on the same daemon the cluster runs on, so no
registry step — but the cluster's nodes each run their own containerd,
so after building, import the image into the node the fleet is pinned to
(`desktop-worker`; the PVC is node-local) and restart the rollout:

```sh
docker build --build-context skills="$HOME/.agents/skills" -t agent-factory:dev .
docker save agent-factory:dev | docker exec -i desktop-worker sh -c \
  'cat > /tmp/df.tar && ctr --namespace k8s.io images import --no-unpack /tmp/df.tar && rm /tmp/df.tar'
kubectl -n agent-factory rollout restart deployment/agent-factory
```

The named `skills` context bakes the machine's `~/.agents/skills` (the
skill library, 012) into the image as a build-time snapshot.
