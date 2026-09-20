export function releaseGesture(long: boolean, target: 'origin' | 'child' | 'outside', cancelled: boolean): 'root' | 'child' | 'keep' | 'cancel' {
  if (cancelled || target === 'outside') return 'cancel';
  if (long) return target === 'child' ? 'child' : 'keep';
  return target === 'origin' ? 'root' : 'cancel';
}
