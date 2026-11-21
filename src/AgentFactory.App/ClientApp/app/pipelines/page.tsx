'use client';

import { useEffect, useRef, useState } from 'react';

import { Alert, AlertDescription, AlertTitle } from '@nanisoft/prism-ui/components/alert';
import { Badge } from '@nanisoft/prism-ui/components/badge';
import { Button } from '@nanisoft/prism-ui/components/button';
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from '@nanisoft/prism-ui/components/card';
import { CtaLink } from '@nanisoft/prism-ui/components/cta-link';
import { Input } from '@nanisoft/prism-ui/components/input';
import { ListPanel } from '@nanisoft/prism-ui/components/list-panel';
import { NativeSelect } from '@nanisoft/prism-ui/components/native-select';
import {
  SelectableTree,
  type SelectableTreeNode,
} from '@nanisoft/prism-ui/components/selectable-tree';
import { Textarea } from '@nanisoft/prism-ui/components/textarea';

import { PageHeader } from '@/components/page-header';
import { ErrorCard, LoadingCard } from '@/components/reading-state';
import { api } from '@/lib/api';
import { lastSegment, messageOf } from '@/lib/format';
import {
  clonePipeline,
  PLACEHOLDERS,
  renderTemplate,
  slugify,
  uniqueId,
  validatePipeline,
} from '@/lib/pipeline';
import type { Pipeline, PipelineCatalog, ProfileSummary } from '@/lib/types';
import { useApi } from '@/lib/use-api';

/**
 * The pipeline editor. A pipeline is an ordered set of phases; each phase is
 * one engine session with its own prompt template, skill allow-list and
 * optional profile-and-model pin. The preview on the right renders the
 * selected phase against the bundled sample issue; it is advisory, never a gate.
 */
export default function PipelinesPage() {
  const catalog = useApi<PipelineCatalog>('/api/pipelines');

  return (
    <div className="app-page app-page--wide">
      <PageHeader
        title="Pipelines"
        description="The ordered phases a run walks, from the first prompt to the pull request. Each phase is one agent session with its own template, skills and model pin."
        actions={
          <CtaLink href="/targets" variant="outline" size="sm">
            Target assignments
          </CtaLink>
        }
      />
      {catalog.reading.phase === 'loading' ? (
        <LoadingCard label="Reading the pipelines" />
      ) : catalog.reading.phase === 'error' ? (
        <ErrorCard
          title="The pipelines could not be read"
          message={catalog.reading.message}
          onRetry={catalog.refresh}
        />
      ) : (
        <PipelineEditor catalog={catalog.reading.data} reload={catalog.refresh} />
      )}
    </div>
  );
}

/**
 * The rail's key for one phase: the pair of positions, never the phase's own
 * name. A phase is a position in an ordered list (`clonePipeline` copies one
 * without an id), and a key built from the name would collide the moment two
 * phases were called the same thing.
 */
function phaseKey(atPipeline: number, atPhase: number): string {
  return `${atPipeline}:${atPhase}`;
}

function PipelineEditor({ catalog, reload }: { catalog: PipelineCatalog; reload: () => void }) {
  const [working, setWorking] = useState<Pipeline[] | null>(null);
  const [defaultId, setDefaultId] = useState(catalog.defaultPipelineId);
  const [profiles, setProfiles] = useState<ProfileSummary[]>(catalog.profiles);
  const [skills, setSkills] = useState<string[]>(catalog.skills);
  const [pipelineIndex, setPipelineIndex] = useState(0);
  const [phaseIndex, setPhaseIndex] = useState(0);
  const [skillFilter, setSkillFilter] = useState('');
  const [errors, setErrors] = useState<string[]>([]);
  const [flash, setFlash] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const selection = useRef({ pipelineIndex, phaseIndex });
  selection.current = { pipelineIndex, phaseIndex };

  useEffect(() => {
    setWorking(catalog.pipelines.map(clonePipeline));
    setDefaultId(catalog.defaultPipelineId);
    setProfiles(catalog.profiles);
    setSkills(catalog.skills);
    const nextPipeline = Math.min(
      selection.current.pipelineIndex,
      Math.max(0, catalog.pipelines.length - 1),
    );
    const nextPhaseCount = catalog.pipelines[nextPipeline]?.phases.length ?? 1;
    setPipelineIndex(nextPipeline);
    setPhaseIndex(Math.min(selection.current.phaseIndex, Math.max(0, nextPhaseCount - 1)));
  }, [catalog]);

  const pipelines = working;

  if (pipelines === null || pipelines.length === 0) {
    return (
      <Alert>
        <AlertTitle>The store has no pipelines</AlertTitle>
        <AlertDescription>
          The store seeds a Standard pipeline on first run. If this is empty, the config document
          is unreadable; check <code className="app-code">data/config.json</code>.
        </AlertDescription>
      </Alert>
    );
  }

  const pipeline = pipelines[Math.min(pipelineIndex, pipelines.length - 1)] ?? pipelines[0];
  if (pipeline === undefined) {
    return null;
  }
  const phase = pipeline.phases[Math.min(phaseIndex, pipeline.phases.length - 1)] ?? pipeline.phases[0];
  if (phase === undefined) {
    return null;
  }

  const selectedProfile =
    phase.profileId === null ? undefined : profiles.find((profile) => profile.id === phase.profileId);
  const pinDangling = phase.profileId !== null && selectedProfile === undefined;
  const pinOutsidePalette =
    selectedProfile !== undefined &&
    phase.modelId !== null &&
    !selectedProfile.models.includes(phase.modelId);
  const visibleSkills = skills.filter((skill) =>
    skill.toLowerCase().includes(skillFilter.trim().toLowerCase()),
  );

  const updatePipeline = (updater: (next: Pipeline) => void) => {
    setWorking((current) => {
      if (current === null) return current;
      const next = current.map(clonePipeline);
      const target = next[pipelineIndex];
      if (target !== undefined) updater(target);
      return next;
    });
  };

  const updatePhase = (updater: (next: Pipeline['phases'][number]) => void) => {
    updatePipeline((next) => {
      const target = next.phases[phaseIndex];
      if (target !== undefined) updater(target);
    });
  };

  const selectPhase = (nextPipeline: number, nextPhase: number) => {
    setPipelineIndex(nextPipeline);
    setPhaseIndex(nextPhase);
    setErrors([]);
    setFlash(null);
  };

  /*
   * The rail is a set of phases grouped by the pipeline that holds them, and the
   * key is the pair rather than a phase id because the selection is derived from
   * the pair: deleting or moving a phase cannot leave the tree pointing at one
   * that is gone.
   */
  const selectedKey = phaseKey(pipelineIndex, phaseIndex);

  const pickPhase = (key: string) => {
    const [atPipeline, atPhase] = key.split(':');
    const nextPipeline = Number(atPipeline);
    const nextPhase = Number(atPhase);
    if (!Number.isInteger(nextPipeline) || !Number.isInteger(nextPhase)) return;
    if (pipelines[nextPipeline] === undefined) return;
    if (pipelines[nextPipeline]?.phases[nextPhase] === undefined) return;
    selectPhase(nextPipeline, nextPhase);
  };

  const phaseCount = pipelines.reduce((total, entry) => total + entry.phases.length, 0);
  const countLine =
    `${pipelines.length} ${pipelines.length === 1 ? 'pipeline' : 'pipelines'}, ` +
    `${phaseCount} ${phaseCount === 1 ? 'phase' : 'phases'}`;

  const treeNodes: SelectableTreeNode[] = pipelines.map((entry, entryIndex) => ({
    type: 'group',
    title: entry.name,
    meta:
      entry.id === defaultId ? (
        <Badge variant="secondary">default</Badge>
      ) : undefined,
    items: entry.phases.map((treePhase, treePhaseIndex) => {
      const pin =
        treePhase.profileId === null
          ? undefined
          : profiles.find((profile) => profile.id === treePhase.profileId);
      return {
        type: 'item',
        id: phaseKey(entryIndex, treePhaseIndex),
        leading: String(treePhaseIndex + 1),
        title: treePhase.name.length > 0 ? treePhase.name : `phase ${treePhaseIndex + 1}`,
        description:
          treePhase.skills.length === 0 ? 'no skills' : treePhase.skills.join(' · '),
        meta:
          pin !== undefined
            ? treePhase.modelId === null
              ? <Badge variant="warning">no model</Badge>
              : <Badge variant="outline">{lastSegment(treePhase.modelId)}</Badge>
            : treePhase.profileId !== null
              ? <Badge variant="destructive">dangling</Badge>
              : undefined,
      };
    }),
  }));

  const addPhase = () => {
    updatePipeline((next) => {
      next.phases.push({
        name: `phase ${next.phases.length + 1}`,
        skills: [],
        template: '',
        profileId: null,
        modelId: null,
      });
    });
    setPhaseIndex(pipeline.phases.length);
    setErrors([]);
    setFlash(null);
  };

  const movePhase = (delta: number) => {
    const target = phaseIndex + delta;
    if (target < 0 || target >= pipeline.phases.length) return;
    updatePipeline((next) => {
      const [moved] = next.phases.splice(phaseIndex, 1);
      if (moved !== undefined) next.phases.splice(target, 0, moved);
    });
    setPhaseIndex(target);
  };

  const deletePhase = () => {
    if (pipeline.phases.length <= 1) return;
    updatePipeline((next) => {
      next.phases.splice(phaseIndex, 1);
    });
    setPhaseIndex(Math.min(phaseIndex, pipeline.phases.length - 2));
    setErrors([]);
  };

  const newPipelineFromDefault = () => {
    const template = pipelines.find((candidate) => candidate.id === defaultId) ?? pipelines[0];
    if (template === undefined) return;
    const copy = clonePipeline(template);
    copy.id = uniqueId(slugify(`${template.name} copy`), pipelines.map((candidate) => candidate.id));
    copy.name = `${template.name} copy`;
    setWorking([...pipelines, copy]);
    setPipelineIndex(pipelines.length);
    setPhaseIndex(0);
    setErrors([]);
    setFlash(null);
  };

  const save = async (makeDefault: boolean) => {
    setErrors([]);
    setFlash(null);
    if (pipeline.id.length === 0) {
      pipeline.id = uniqueId(slugify(pipeline.name), pipelines.map((candidate) => candidate.id));
    }
    const validation = validatePipeline(pipeline, profiles, skills);
    if (validation.length > 0) {
      setErrors(validation);
      return;
    }

    setBusy(true);
    try {
      await api.savePipeline(pipeline);
      if (makeDefault) {
        await api.setDefaultPipeline(pipeline.id);
      }
      setFlash(makeDefault ? 'Saved — this is now the fleet default' : 'Saved');
      reload();
    } catch (error) {
      setErrors([messageOf(error)]);
    } finally {
      setBusy(false);
    }
  };

  const remove = async () => {
    setErrors([]);
    setFlash(null);
    setBusy(true);
    try {
      await api.deletePipeline(pipeline.id);
      setPipelineIndex(0);
      setPhaseIndex(0);
      setFlash('Deleted');
      reload();
    } catch (error) {
      setErrors([messageOf(error)]);
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="app-pipeline">
      <ListPanel
        className="app-pipeline__tree"
        title="Pipelines"
        count={countLine}
        actions={
          <Button type="button" size="sm" variant="ghost" onClick={addPhase}>
            Add phase
          </Button>
        }
        scroll={false}
        footer={
          <div className="app-pipeline__footer">
            <Button
              type="button"
              size="sm"
              variant="outline"
              onClick={newPipelineFromDefault}
            >
              New pipeline
            </Button>
            <span className="app-hint">Starts as a copy of the default</span>
          </div>
        }
      >
        <SelectableTree
          className="app-pipeline__tree-body"
          nodes={treeNodes}
          value={selectedKey}
          onValueChange={pickPhase}
          label="Pipelines and their phases"
        />
      </ListPanel>

      <Card className="app-pipeline__editor">
        <CardHeader>
          <div className="app-editor-head">
            <div className="app-editor-head__text">
              <CardTitle as="h2">
                {phase.name.length > 0 ? phase.name : `phase ${phaseIndex + 1}`}
              </CardTitle>
              <CardDescription>
                Phase {Math.min(phaseIndex, pipeline.phases.length - 1) + 1} of{' '}
                {pipeline.phases.length} in {pipeline.name}
              </CardDescription>
            </div>
            <div className="app-editor-actions">
              <Button
                type="button"
                size="sm"
                variant="outline"
                onClick={() => movePhase(-1)}
                disabled={phaseIndex === 0}
                aria-label="Move phase up"
              >
                ↑
              </Button>
              <Button
                type="button"
                size="sm"
                variant="outline"
                onClick={() => movePhase(1)}
                disabled={phaseIndex >= pipeline.phases.length - 1}
                aria-label="Move phase down"
              >
                ↓
              </Button>
              <Button
                type="button"
                size="sm"
                variant="outline"
                onClick={deletePhase}
                disabled={pipeline.phases.length <= 1}
                aria-label="Delete phase"
              >
                ✕
              </Button>
            </div>
          </div>
        </CardHeader>
        <CardContent>
          <div className="app-stack">
            <div className="app-field-row">
              <div className="app-field">
                <label className="app-label" htmlFor="phase-profile">
                  Agent profile
                </label>
                <NativeSelect
                  id="phase-profile"
                  size="sm"
                  value={phase.profileId ?? ''}
                  onChange={(event) => {
                    const value = event.target.value;
                    updatePhase((next) => {
                      next.profileId = value.length > 0 ? value : null;
                      next.modelId = null;
                    });
                    setErrors([]);
                  }}
                >
                  <option value="">(engine default — as today)</option>
                  {profiles
                    .filter((profile) => profile.id.length > 0)
                    .map((profile) => (
                      <option key={profile.id} value={profile.id}>
                        {profile.name}
                      </option>
                    ))}
                  {pinDangling ? (
                    <option value={phase.profileId ?? ''}>⚠ {phase.profileId} — missing</option>
                  ) : null}
                </NativeSelect>
                <p className="app-hint">Pins are atomic: profile and model together, or neither.</p>
              </div>
              <div className="app-field">
                <label className="app-label" htmlFor="phase-model">
                  Model
                </label>
                <NativeSelect
                  id="phase-model"
                  size="sm"
                  value={phase.modelId ?? ''}
                  disabled={selectedProfile === undefined}
                  onChange={(event) => {
                    const value = event.target.value;
                    updatePhase((next) => {
                      next.modelId = value.length > 0 ? value : null;
                    });
                  }}
                >
                  {selectedProfile === undefined ? (
                    pinDangling ? (
                      <option value="">profile missing</option>
                    ) : (
                      <option value="">—</option>
                    )
                  ) : (
                    <>
                      <option value="">(pick a model…)</option>
                      {selectedProfile.models.map((model) => (
                        <option key={model} value={model}>
                          {model}
                        </option>
                      ))}
                      {pinOutsidePalette ? (
                        <option value={phase.modelId ?? ''}>⚠ {phase.modelId} (outside palette)</option>
                      ) : null}
                    </>
                  )}
                </NativeSelect>
                <p className="app-hint">
                  {selectedProfile === undefined
                    ? pinDangling
                      ? 'palette unavailable — profile missing'
                      : 'no profile pinned — engine default'
                    : `options = ${selectedProfile.name}'s palette only`}
                </p>
              </div>
            </div>

            {pinDangling ? (
              <Alert variant="destructive">
                <AlertTitle>Dangling pin</AlertTitle>
                <AlertDescription>
                  Profile &quot;{phase.profileId}&quot; no longer exists. Re-pin or clear the pin —
                  runs fail fast until then.
                </AlertDescription>
              </Alert>
            ) : selectedProfile !== undefined ? (
              <>
                {pinOutsidePalette ? (
                  <Alert variant="destructive">
                    <AlertTitle>Model outside the palette</AlertTitle>
                    <AlertDescription>
                      {phase.modelId} is not in {selectedProfile.name}&apos;s palette. Saves are
                      rejected and runs fail fast.
                    </AlertDescription>
                  </Alert>
                ) : null}
              </>
            ) : null}

            <div className="app-field">
              <label className="app-label" htmlFor="phase-name">
                Phase name
              </label>
              <Input
                id="phase-name"
                value={phase.name}
                onChange={(event) => {
                  const value = event.target.value;
                  updatePhase((next) => {
                    next.name = value;
                  });
                }}
              />
            </div>

            <div className="app-field">
              <span className="app-label">Skill set (allow-list — click to toggle)</span>
              <Input
                placeholder={`filter ${skills.length} skills…`}
                value={skillFilter}
                onChange={(event) => setSkillFilter(event.target.value)}
              />
              <div className="app-skills">
                {visibleSkills.map((skill) => {
                  const on = phase.skills.includes(skill);
                  return (
                    <Button
                      key={skill}
                      type="button"
                      size="sm"
                      variant={on ? 'default' : 'outline'}
                      aria-pressed={on}
                      onClick={() => {
                        updatePhase((next) => {
                          next.skills = on
                            ? next.skills.filter((candidate) => candidate !== skill)
                            : [...next.skills, skill];
                        });
                      }}
                    >
                      {on ? `✓ ${skill}` : skill}
                    </Button>
                  );
                })}
              </div>
              <p className="app-hint">
                Enforced per-worktree by the engine&apos;s permission config; the session cannot
                see unticked skills.
              </p>
            </div>

            <div className="app-field">
              <label className="app-label" htmlFor="phase-template">
                Initial prompt (full standalone template)
              </label>
              <Textarea
                id="phase-template"
                className="app-template"
                value={phase.template}
                onChange={(event) => {
                  const value = event.target.value;
                  updatePhase((next) => {
                    next.template = value;
                  });
                }}
              />
              <p className="app-hint">
                Placeholders: {PLACEHOLDERS.map((name) => `{{${name}}}`).join(', ')}
              </p>
            </div>

            <div className="app-row">
              <Button type="button" size="sm" onClick={() => void save(false)} disabled={busy}>
                Save pipeline
              </Button>
              <Button
                type="button"
                size="sm"
                variant="outline"
                onClick={() => void save(true)}
                disabled={busy}
              >
                Set as fleet default
              </Button>
              <Button
                type="button"
                size="sm"
                variant="destructive"
                onClick={() => void remove()}
                disabled={busy || pipeline.id === defaultId}
              >
                Delete pipeline
              </Button>
              {flash === null ? null : <Badge variant="success">{flash}</Badge>}
            </div>

            {errors.length === 0 ? null : (
              <Alert variant="destructive">
                <AlertTitle>Save rejected</AlertTitle>
                <AlertDescription>
                  <ul className="app-errors">
                    {errors.map((error) => (
                      <li key={error}>{error}</li>
                    ))}
                  </ul>
                </AlertDescription>
              </Alert>
            )}
          </div>
        </CardContent>
      </Card>

      <Card className="app-pipeline__preview">
        <CardHeader>
          <CardTitle as="h2">Preview (sample issue)</CardTitle>
        </CardHeader>
        <CardContent>
          <pre className="app-pre app-preview">{renderTemplate(phase.template)}</pre>
          <p className="app-hint">
            Rendered with the bundled sample values for every placeholder. It is advisory: the run
            renders the template against the real issue.
          </p>
        </CardContent>
      </Card>
    </div>
  );
}
