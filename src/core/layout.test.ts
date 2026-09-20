import { describe, it, expect } from 'vitest';
import { computeLayout } from './layout';
describe('adaptive layout', () => {
  it('keeps user preferred size on a roomy display', () => {
    const r = computeLayout(640, 88, 40, 1200, 1);
    expect(r.width).toBe(640); expect(r.icon).toBe(40); expect(r.font).toBe(14);
  });
  it('keeps text readable with large system text and a short dock', () => {
    const r = computeLayout(640, 64, 64, 1200, 2);
    expect(r.font).toBeGreaterThanOrEqual(24);
    expect(r.height).toBeGreaterThanOrEqual(r.icon + r.font * 1.35 + 26);
  });
  it('fits narrow viewports without destroying preferred settings', () => {
    const r = computeLayout(1120, 88, 40, 360, 1);
    expect(r.width).toBeLessThanOrEqual(336); expect(r.capacity).toBeGreaterThanOrEqual(1);
  });
  it('reserves room for the additional organize action', () => {
    const r = computeLayout(640, 88, 40, 1200, 1, 132);
    expect(r.capacity * (r.cell + 8) - 8 + 32 + 132).toBeLessThanOrEqual(r.width);
  });
  it('grows from the saved width to fit pinned top-level projects', () => {
    const r = computeLayout(320, 88, 40, 1200, 1, 136, 6);
    expect(r.width).toBe(592);
    expect(r.capacity).toBe(6);
  });
  it('shrinks with the pinned count but never below the saved width', () => {
    expect(computeLayout(640, 88, 40, 1200, 1, 136, 2).width).toBe(640);
  });
  it('caps adaptive growth at both 1120 and the available viewport', () => {
    expect(computeLayout(320, 88, 40, 1600, 1, 136, 30).width).toBe(1120);
    const narrow = computeLayout(320, 88, 40, 700, 1, 136, 30);
    expect(narrow.width).toBe(676);
    expect(narrow.capacity).toBe(7);
  });
});
