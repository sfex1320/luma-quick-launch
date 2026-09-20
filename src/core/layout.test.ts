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
});
