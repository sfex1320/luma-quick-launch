import { describe, it, expect } from 'vitest';
import { StateSchema, HostEventSchema, resultSchemas, LaunchCommandSchema } from './contracts';
import { initialState } from './data';
describe('boundary validation', () => {
  it('preserves literal manual commands and validates working-directory controls', () => {
    const launch = { command: 'echo hello ', workingDirectory: 'C:\\Projects\\Repo' };
    expect(LaunchCommandSchema.parse(launch)).toEqual(launch);
    for (const command of ['', ' ', 'echo ok\nexit', 'x'.repeat(1001)]) expect(LaunchCommandSchema.safeParse({ ...launch, command }).success).toBe(false);
    for (const workingDirectory of ['relative', 'C:\\repo\nother', 'C:\\repo\0other']) expect(LaunchCommandSchema.safeParse({ ...launch, workingDirectory }).success).toBe(false);
  });
  it('rejects duplicate project identities', () => {
    const state = structuredClone(initialState); state.projects.push(state.projects[0]);
    expect(StateSchema.safeParse(state).success).toBe(false);
  });
  it('rejects out-of-range appearance and empty primary item', () => {
    const state = structuredClone(initialState); state.preferences.width = 10;
    expect(StateSchema.safeParse(state).success).toBe(false);
    state.preferences.width = 640; state.projects[0].items = [];
    expect(StateSchema.safeParse(state).success).toBe(false);
  });
  it('rejects malformed native response and events', () => {
    expect(resultSchemas['shell.openItem'].safeParse({ accepted: 'yes' }).success).toBe(false);
    expect(HostEventSchema.safeParse({ event: 'window.visibility', data: { visible: 'false' } }).success).toBe(false);
  });
  it('accepts cancelled folder picker as null', () => expect(resultSchemas['shell.pickFolder'].safeParse(null).success).toBe(true));
  it('preserves visibility generations and rejects invalid sequence numbers', () => {
    const event = { event: 'window.visibility', data: { visible: true, visibilityId: 23 } };
    expect(HostEventSchema.parse(event)).toEqual(event);
    for (const visibilityId of [-1, 0.1, Number.MAX_SAFE_INTEGER + 1, '23']) {
      expect(HostEventSchema.safeParse({ ...event, data: { visible: true, visibilityId } }).success).toBe(false);
    }
    expect(HostEventSchema.safeParse({ event: 'window.visibility', data: { visible: false } }).success).toBe(true);
  });
});
