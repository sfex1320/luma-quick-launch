import { describe, expect, it } from 'vitest';
import { PreferencesSchema } from '../contracts';
import { defaults } from '../data';
import { bindingFromKey, formatHotkey, matchPanelShortcut, shortcutTargetLabel } from './hotkeys';

const binding = { id: 'one', code: 'KeyA', ctrl: false, shift: false, alt: false, scope: 'panel' as const, action: 'search' as const };
const event = { code: 'KeyA', ctrlKey: false, shiftKey: false, altKey: false, metaKey: false, repeat: false, isComposing: false };
describe('configurable shortcuts', () => {
  it('migrates old preferences without shortcuts', () => expect(PreferencesSchema.safeParse(defaults).success).toBe(true));
  it('accepts a panel-only single key', () => expect(PreferencesSchema.safeParse({ ...defaults, shortcuts: [binding] }).success).toBe(true));
  it('rejects global single keys', () => expect(PreferencesSchema.safeParse({ ...defaults, shortcuts: [{ ...binding, scope: 'global' }] }).success).toBe(false));
  it('rejects modifier-only keys', () => expect(PreferencesSchema.safeParse({ ...defaults, shortcuts: [{ ...binding, code: 'ShiftLeft' }] }).success).toBe(false));
  it('rejects whitespace ids consistently with the host', () => { for (const change of [{ id: ' ' }, { action: 'project', projectId: ' ' }, { action: 'item', projectId: 'p', itemId: '\t' }]) expect(PreferencesSchema.safeParse({ ...defaults, shortcuts: [{ ...binding, ...change }] }).success).toBe(false); });
  it('normalizes Enter virtual key collisions', () => expect(PreferencesSchema.safeParse({ ...defaults, shortcuts: [{ ...binding, code: 'Enter' }, { ...binding, id: 'two', code: 'NumpadEnter' }] }).success).toBe(false));
  it('rejects duplicate chords across scopes', () => expect(PreferencesSchema.safeParse({ ...defaults, shortcuts: [{ ...binding, ctrl: true }, { ...binding, id: 'two', ctrl: true, scope: 'global' }] }).success).toBe(false));
  it('reserves the existing search shortcut', () => expect(PreferencesSchema.safeParse({ ...defaults, shortcuts: [{ ...binding, code: 'Space', ctrl: true, alt: true }] }).success).toBe(false));
  it('allows all three modifiers', () => expect(PreferencesSchema.safeParse({ ...defaults, shortcuts: [{ ...binding, ctrl: true, shift: true, alt: true, scope: 'global' }] }).success).toBe(true));
  it('requires both saved IDs for item actions', () => expect(PreferencesSchema.safeParse({ ...defaults, shortcuts: [{ ...binding, action: 'item', projectId: 'p' }] }).success).toBe(false));
  it('does not allow transient directory actions globally', () => expect(PreferencesSchema.safeParse({ ...defaults, shortcuts: [{ ...binding, ctrl: true, scope: 'global', action: 'newFolder' }] }).success).toBe(false));
  it('does not allow extra IDs for functions', () => expect(PreferencesSchema.safeParse({ ...defaults, shortcuts: [{ ...binding, projectId: 'wrong' }] }).success).toBe(false));
  it('retains valid bindings in parsed preferences', () => expect(PreferencesSchema.parse({ ...defaults, shortcuts: [binding] }).shortcuts).toEqual([binding]));
  it('records the physical key and modifiers', () => expect(bindingFromKey({ ...event, ctrlKey: true, shiftKey: true })).toEqual({ code: 'KeyA', ctrl: true, shift: true, alt: false }));
  it('does not record modifier-only/IME/Windows keys', () => { expect(bindingFromKey({ ...event, code: 'AltLeft' })).toBeNull(); expect(bindingFromKey({ ...event, isComposing: true })).toBeNull(); expect(bindingFromKey({ ...event, metaKey: true })).toBeNull(); });
  it('formats symbols for users', () => expect(formatHotkey({ ...binding, ctrl: true, shift: true, alt: true })).toBe('Ctrl + Shift + Alt + A'));
  it('matches panel keys only when visible and focused', () => { expect(matchPanelShortcut([binding], event, { focused: true, visible: true, editing: false })).toEqual(binding); expect(matchPanelShortcut([binding], event, { focused: false, visible: true, editing: false })).toBeUndefined(); });
  it('does not trigger while input, hidden, repeat, IME or a global binding', () => {
    for (const context of [{ focused: true, visible: false, editing: false }, { focused: true, visible: true, editing: true }]) expect(matchPanelShortcut([binding], event, context)).toBeUndefined();
    for (const extra of [{ repeat: true }, { isComposing: true }, { metaKey: true }, { altKey: true }]) expect(matchPanelShortcut([binding], { ...event, ...extra }, { focused: true, visible: true, editing: false })).toBeUndefined();
    expect(matchPanelShortcut([{ ...binding, scope: 'global' }], event, { focused: true, visible: true, editing: false })).toBeUndefined();
  });
  it('labels deleted saved targets without falling back to paths', () => expect(shortcutTargetLabel({ ...binding, action: 'item', projectId: 'missing', itemId: 'gone' }, [])).toBe('目标已失效'));
});
