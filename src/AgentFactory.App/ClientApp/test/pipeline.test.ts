import { describe, expect, it } from 'vitest';

import type { Pipeline, ProfileSummary } from '@/lib/types';
import {
  renderTemplate,
  slugify,
  uniqueId,
  unknownPlaceholders,
  validatePipeline,
} from '@/lib/pipeline';

const profiles: ProfileSummary[] = [
  {
    id: 'go',
    name: 'Go',
    flavour: 'OpencodeGo',
    flavourLabel: 'opencode-go',
    models: ['opencode-go/deepseek-v4.1-flash'],
    hasKey: false,
  },
];

const pipeline: Pipeline = {
  id: 'standard',
  name: 'Standard',
  phases: [
    {
      name: 'develop',
      skills: ['tdd'],
      template: 'Implement {{issue_title}} in {{repo}}.',
      profileId: null,
      modelId: null,
    },
  ],
};

describe('pipeline helpers', () => {
  it('renders the six placeholders and leaves unknown ones alone', () => {
    expect(renderTemplate('{{issue_title}} on {{repo}}')).toContain('Add retry backoff');
    expect(renderTemplate('{{issue_title}} on {{repo}}')).toContain('demofactory-playground');
    expect(unknownPlaceholders('{{issue_title}} {{not_a_placeholder}}')).toEqual([
      'not_a_placeholder',
    ]);
  });

  it('slugifies and de-duplicates ids', () => {
    expect(slugify('Standard Copy!')).toBe('standard-copy');
    expect(uniqueId('standard', ['standard', 'standard-2'])).toBe('standard-3');
  });

  it('accepts a valid pipeline', () => {
    expect(validatePipeline(pipeline, profiles, ['tdd'])).toEqual([]);
  });

  it('rejects a pin without its model', () => {
    const pinned: Pipeline = {
      ...pipeline,
      phases: [
        {
          ...pipeline.phases[0]!,
          profileId: 'go',
          modelId: null,
        },
      ],
    };
    expect(validatePipeline(pinned, profiles, ['tdd'])).toEqual([
      "phase 1 'develop': pin is atomic — pick a model from 'Go's palette, or clear the profile",
    ]);
  });

  it('rejects a model outside the profile palette', () => {
    const outside: Pipeline = {
      ...pipeline,
      phases: [
        {
          ...pipeline.phases[0]!,
          profileId: 'go',
          modelId: 'opencode-go/other',
        },
      ],
    };
    expect(validatePipeline(outside, profiles, ['tdd'])).toContain(
      "phase 1 'develop': pinned model 'opencode-go/other' is not in 'Go's palette",
    );
  });

  it('rejects an unknown skill and an unknown placeholder', () => {
    const broken: Pipeline = {
      ...pipeline,
      phases: [
        {
          ...pipeline.phases[0]!,
          skills: ['not-a-skill'],
          template: '{{nope}}',
        },
      ],
    };
    const errors = validatePipeline(broken, profiles, ['tdd']);
    expect(errors.some((error) => error.includes("unknown skill 'not-a-skill'"))).toBe(true);
    expect(errors.some((error) => error.includes('unknown placeholder {{nope}}'))).toBe(true);
  });
});
