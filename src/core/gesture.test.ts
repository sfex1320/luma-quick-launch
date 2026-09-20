import { describe, it, expect } from 'vitest';
import { releaseGesture } from './gesture';
describe('long press release', () => {
  it('short press opens root once', () => expect(releaseGesture(false, 'origin', false)).toBe('root'));
  it('long press never also opens root', () => expect(releaseGesture(true, 'origin', false)).toBe('keep'));
  it('long press release over a child launches child', () => expect(releaseGesture(true, 'child', false)).toBe('child'));
  it('release outside cancels', () => expect(releaseGesture(true, 'outside', false)).toBe('cancel'));
  it('a cancelled drag cannot launch', () => expect(releaseGesture(false, 'origin', true)).toBe('cancel'));
});
