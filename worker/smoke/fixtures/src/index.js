# The synthetic project the smoke test builds in.
#
# It exists so the round has a real git repository with a real base commit to be
# observed against, without the test needing a bind mount -- and ADR-0010 says a
# worker container gets no host paths at all. So the tree is baked into a derived
# image instead, which is the documented way to add things to a project image
# anyway (docs/deriving-a-project-image.md).

export function greet() {
  return "hello";
}
