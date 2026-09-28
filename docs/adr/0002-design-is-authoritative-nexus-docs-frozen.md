# DESIGN.md is authoritative while building; the published Nexus docs are frozen

`DESIGN.md` is the specification we build against. The deeper, already-published
version of the same design in the `nexus` repository's `content/docs/` corpus is
frozen and treated as accepted debt: we do not edit it while implementing, and we
reconcile it in one sweep when the first working milestone lands.

We chose this over keeping the published docs current throughout, because
implementation discovers things the design got wrong, and making every such discovery
a docs edit before work can continue would tax the exact phase where we most need to
move fast. The alternative — freezing without a scheduled reconciliation — would let a
public site slowly describe a system that no longer matches reality, so the
reconciliation is a checkpoint rather than an intention.

`nexus/CONSISTENCY.md` is explicitly out of scope for this decision. It is a
cross-repo style and dependency contract, not a design input, and nothing in it
constrains how this repository is built.

# Consequences

The published docs at nexus.nanisoft.com/docs are, until reconciliation, a
description of an intended system rather than a description of this one. Anyone
reading them for operational guidance during that window will be misled, and that is
a known and accepted cost rather than an oversight.

Reconciliation is a scheduled task with a trigger, not a thing we remember to do. It
belongs in the build plan as its own ticket, gated on the first milestone working,
and it must cover the blog posts as well as the docs corpus, since both describe the
same system to the same audience.
