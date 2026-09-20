export function computeLayout(width: number, height: number, icon: number, viewport: number, textScale = 1, actionWidth = 96) {
  const clamp = (n: number, min: number, max: number) => Math.min(max, Math.max(min, n));
  const actualWidth = Math.min(clamp(width, 320, 1120), Math.max(160, viewport - 24));
  let actualIcon = clamp(icon, 24, 64);
  const fontFor = (size: number) => clamp(Math.round(size * .25 + 4), 12, 18) * textScale;
  while (actualIcon > 24 && actualIcon + Math.ceil(fontFor(actualIcon) * 1.35) + 26 > height) actualIcon--;
  const font = fontFor(actualIcon);
  const actualHeight = Math.max(height, actualIcon + Math.ceil(font * 1.35) + 26);
  const cell = Math.max(64, actualIcon + 24);
  const capacity = Math.max(1, Math.floor((actualWidth - 32 - actionWidth + 8) / (cell + 8)));
  return { width: actualWidth, height: actualHeight, icon: actualIcon, font, capacity, cell };
}
