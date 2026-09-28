# The smoke test's own image.
#
# It is a project image in the sense docs/deriving-a-project-image.md describes:
# the base worker image plus a project of its own. The smoke test needs a
# repository with a base commit to observe, and a worker container is given no
# host path to put one in (ADR-0010), so the repository is baked in here instead.
#
# Nothing here overrides the base image's entrypoint. The round runs on the base
# image's own machinery; this image only supplies the project it builds.

ARG BASE_IMAGE=ghcr.io/nanisoft/agent-factory-worker:1
FROM ${BASE_IMAGE}

USER root
COPY fixtures/ /opt/smoke/project/
COPY round.sh   /opt/smoke/round.sh
COPY expected/  /opt/smoke/expected/
RUN set -eux; \
    mkdir -p /work; \
    cp -a /opt/smoke/project/. /work/; \
    chown -R agent:agent /work; \
    chmod 0755 /work/scripts/test.sh /opt/smoke/round.sh; \
    # A real base commit, so the round has something to be diffed against.
    su agent -c 'cd /work && git init -q -b main . \
        && git add -A \
        && git commit -q -m "base of the synthetic project"' \
    # The expected content of the file the round changes, kept outside /work so it
    # can never appear in the diff a reviewer is asked to judge.
    && chown -R agent:agent /opt/smoke

USER agent:agent
WORKDIR /work

# The round. Everything after this point is the base image's own machinery: the
# entrypoint, the recording wrapper, the collector, the result file.
CMD ["exec", "bash", "-c", "cd /work && run -o 'the round' bash /opt/smoke/round.sh"]
