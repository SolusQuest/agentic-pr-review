import { Ajv } from 'ajv';
import { describe, expect, it } from 'vitest';
import registrationSchema from '../../protocol/schemas/candidate-registration.v1.json' with { type: 'json' };
import markerSchema from '../../protocol/schemas/accepted-state-marker.v1.json' with { type: 'json' };
import selectorSchema from '../../protocol/schemas/state-selector.v1.json' with { type: 'json' };

describe('M4 state acceptance schemas', () => {
  it('compiles all closed schemas in strict Ajv mode', () => {
    const ajv = new Ajv({ strict: true, allErrors: true });
    expect(() => ajv.compile(registrationSchema)).not.toThrow();
    expect(() => ajv.compile(markerSchema)).not.toThrow();
    expect(() => ajv.compile(selectorSchema)).not.toThrow();
  });

  it('keeps nested state key and transition definitions closed', () => {
    for (const schema of [registrationSchema, markerSchema, selectorSchema]) {
      const defs = schema.$defs as Record<string, unknown>;
      expect(defs.stateKey).toMatchObject({ type: 'object', additionalProperties: false });
      expect(defs.transition).toMatchObject({ oneOf: expect.any(Array) });
    }
  });
});
