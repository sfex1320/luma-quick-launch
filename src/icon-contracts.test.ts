import { expect, it } from 'vitest';
import { resultSchemas } from './contracts';

const png = 'data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aWZkAAAAASUVORK5CYII=';
it('only accepts bounded PNG icon payloads or null', () => {
  const schema = (resultSchemas as unknown as Record<string, { safeParse: (value: unknown) => { success: boolean } }>)['shell.getIcon'];
  expect(schema, 'shell.getIcon must validate native payloads').toBeDefined();
  expect(schema.safeParse({ dataUrl: png }).success).toBe(true);
  expect(schema.safeParse({ dataUrl: null }).success).toBe(true);
  for (const dataUrl of ['', 'https://example.com/icon.png', 'data:image/svg+xml,<svg/>', 'data:image/png;base64,AAAA', png + 'A'.repeat(350000)]) {
    expect(schema.safeParse({ dataUrl }).success).toBe(false);
  }
});
