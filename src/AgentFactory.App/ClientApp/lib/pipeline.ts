import type { Pipeline, ProfileSummary } from './types';

/** The six placeholders the factory guarantees. */
export const PLACEHOLDERS = [
  'issue_title',
  'issue_body',
  'repo',
  'branch',
  'base',
  'definition_of_done',
] as const;

/** The bundled sample issue the preview renders against. */
export const SAMPLE_ISSUE: Record<(typeof PLACEHOLDERS)[number], string> = {
  issue_title: 'Add retry backoff to the GitHub poller',
  issue_body:
    'The poller hammers the API when GitHub is degraded.\n\n' +
    'Add exponential backoff (1s to 30s cap) and log the current backoff at debug.',
  repo: 'DurgaPrasadReddyV/demofactory-playground',
  branch: 'agent/issue-12-1',
  base: 'main',
  definition_of_done: 'dotnet build && dotnet test',
};

export function renderTemplate(template: string): string {
  return PLACEHOLDERS.reduce(
    (rendered, placeholder) =>
      rendered.replaceAll(`{{${placeholder}}}`, SAMPLE_ISSUE[placeholder]),
    template,
  );
}

export function unknownPlaceholders(template: string): string[] {
  const found = new Set<string>();
  for (const match of template.matchAll(/\{\{(\w+)\}\}/g)) {
    const name = match[1];
    if (name !== undefined && !PLACEHOLDERS.includes(name as (typeof PLACEHOLDERS)[number])) {
      found.add(name);
    }
  }
  return [...found];
}

export function slugify(name: string): string {
  const slug = name
    .toLowerCase()
    .split('')
    .map((character) => (/[a-z0-9]/.test(character) ? character : '-'))
    .join('')
    .replaceAll(/-+/g, '-')
    .replace(/^-|-$/g, '');
  return slug.length > 0 ? slug : 'pipeline';
}

export function uniqueId(base: string, existing: readonly string[]): string {
  const taken = new Set(existing);
  if (!taken.has(base)) {
    return base;
  }
  let n = 2;
  while (taken.has(`${base}-${n}`)) {
    n += 1;
  }
  return `${base}-${n}`;
}

export function clonePipeline(pipeline: Pipeline): Pipeline {
  return {
    id: pipeline.id,
    name: pipeline.name,
    phases: pipeline.phases.map((phase) => ({
      name: phase.name,
      skills: [...phase.skills],
      template: phase.template,
      profileId: phase.profileId,
      modelId: phase.modelId,
    })),
  };
}

/**
 * The same rules the API applies, run here first so a refusal appears beside
 * the editor instead of after a round trip. The API remains authoritative.
 */
export function validatePipeline(
  pipeline: Pipeline,
  profiles: readonly ProfileSummary[],
  skills: readonly string[],
): string[] {
  const errors: string[] = [];
  if (pipeline.name.trim().length === 0) {
    errors.push('pipeline: name is empty');
  }
  if (pipeline.phases.length === 0) {
    errors.push('pipeline: at least one phase is required');
  }

  pipeline.phases.forEach((phase, index) => {
    const label = `phase ${index + 1} '${phase.name}'`;
    if (phase.name.trim().length === 0) {
      errors.push(`${label}: name is empty`);
    }
    if (phase.template.trim().length === 0) {
      errors.push(`${label}: template is empty`);
    }
    for (const placeholder of unknownPlaceholders(phase.template)) {
      errors.push(
        `${label}: unknown placeholder {{${placeholder}}} — allowed: ` +
          PLACEHOLDERS.map((name) => `{{${name}}}`).join(', '),
      );
    }
    for (const unknown of phase.skills.filter((skill) => !skills.includes(skill))) {
      errors.push(`${label}: unknown skill '${unknown}' — not in the host-side skill library`);
    }

    if (phase.profileId !== null && phase.profileId.length > 0) {
      const profile = profiles.find((candidate) => candidate.id === phase.profileId);
      if (profile === undefined) {
        errors.push(
          `${label}: dangling pin — profile '${phase.profileId}' no longer exists; re-pin or clear`,
        );
      } else if (phase.modelId === null || phase.modelId.length === 0) {
        errors.push(
          `${label}: pin is atomic — pick a model from '${profile.name}'s palette, or clear the profile`,
        );
      } else if (!profile.models.includes(phase.modelId)) {
        errors.push(
          `${label}: pinned model '${phase.modelId}' is not in '${profile.name}'s palette`,
        );
      }
    } else if (phase.modelId !== null && phase.modelId.length > 0) {
      errors.push(`${label}: model pinned without a profile — pins are atomic`);
    }
  });

  return errors;
}
