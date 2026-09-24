import { describe, expect, it } from 'vitest';
import { readTransferSource, transferOperation } from './fileTransfer';
describe('file transfer intent', () => {
  it('copies by default and Ctrl, moves with Shift, creates links with Alt', () => {
    expect(transferOperation({ altKey: false, shiftKey: false })).toBe('copy');
    expect(transferOperation({ altKey: false, shiftKey: false, ctrlKey: true })).toBe('copy');
    expect(transferOperation({ altKey: false, shiftKey: true, ctrlKey: true })).toBe('move');
    expect(transferOperation({ altKey: true, shiftKey: true })).toBe('link');
  });
  it('only extracts saved IDs, never caller paths', () => {
    expect(readTransferSource('{"projectId":"p","itemId":"i","path":"C:/bad"}', false)).toEqual({projectId:'p',itemId:'i'});
    expect(readTransferSource('{"projectId":"p","itemId":"i"}', true)).toBeNull();
    expect(readTransferSource('{"projectId":[],"itemId":"i"}', false)).toBeNull();
  });
});
