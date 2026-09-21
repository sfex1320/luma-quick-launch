import type { Project, ShortcutBinding } from '../contracts';

export const validHotkeyCode = (code: string) => /^(Key[A-Z]|Digit[0-9]|F(?:[1-9]|1[0-9]|2[0-4])|Numpad(?:[0-9]|Add|Subtract|Multiply|Divide|Decimal|Enter)|Space|Enter|Tab|Escape|Arrow(?:Up|Down|Left|Right)|Home|End|PageUp|PageDown|Insert|Delete|Backspace|Backquote|Minus|Equal|BracketLeft|BracketRight|Backslash|Semicolon|Quote|Comma|Period|Slash)$/.test(code);
export const contextActions = ['openDirectory', 'newFolder', 'copyAddress', 'runProject'] as const;
export const hotkeyActions = { dock: '显示主工具条', search: '搜索', settings: '设置', edit: '整理快捷项', project: '进入项目组 / 子菜单', item: '打开软件、文件夹或文件', openDirectory: '打开当前目录', newFolder: '新建文件夹', copyAddress: '复制当前目录地址', runProject: '打开项目软件' } as const;
type Chord = Pick<ShortcutBinding, 'code' | 'ctrl' | 'shift' | 'alt'>;
type KeyInput = { code: string; ctrlKey: boolean; shiftKey: boolean; altKey: boolean; metaKey: boolean; repeat: boolean; isComposing: boolean };
export const chordId = (chord: Chord) => `${+chord.ctrl}${+chord.shift}${+chord.alt}:${chord.code === 'NumpadEnter' ? 'Enter' : chord.code}`;
export const reservedChord = (chord: Chord) => chord.ctrl && !chord.shift && (chord.code === 'KeyK' && !chord.alt || chord.code === 'Space' && chord.alt);
export function bindingFromKey(event: KeyInput): Chord | null {
  if (event.isComposing || event.repeat || event.metaKey || !validHotkeyCode(event.code)) return null;
  return { code: event.code, ctrl: event.ctrlKey, shift: event.shiftKey, alt: event.altKey };
}
const names: Record<string, string> = { Space: '空格', Escape: 'Esc', ArrowUp: '↑', ArrowDown: '↓', ArrowLeft: '←', ArrowRight: '→', Backquote: '`', Minus: '-', Equal: '=', BracketLeft: '[', BracketRight: ']', Backslash: '\\', Semicolon: ';', Quote: "'", Comma: ',', Period: '.', Slash: '/', NumpadEnter: '数字区 Enter' };
export function formatHotkey(chord: Chord) {
  const key = names[chord.code] ?? chord.code.replace(/^Key|^Digit/, '').replace(/^Numpad/, '数字区 ');
  return [...(chord.ctrl ? ['Ctrl'] : []), ...(chord.shift ? ['Shift'] : []), ...(chord.alt ? ['Alt'] : []), key].join(' + ');
}
export function matchPanelShortcut(bindings: ShortcutBinding[], event: KeyInput, context: { focused: boolean; visible: boolean; editing: boolean }) {
  if (!context.focused || !context.visible || context.editing) return;
  const chord = bindingFromKey(event);
  if (chord) return bindings.find(binding => binding.scope === 'panel' && chordId(binding) === chordId(chord));
}
export function shortcutTargetLabel(binding: ShortcutBinding, projects: Project[]) {
  if (binding.action !== 'project' && binding.action !== 'item') return hotkeyActions[binding.action];
  const project = projects.find(project => project.id === binding.projectId);
  if (!project) return '目标已失效';
  if (binding.action === 'project') return `进入 · ${project.name}`;
  const item = project.items.find(item => item.id === binding.itemId);
  return item ? `${project.name} / ${item.name}` : '目标已失效';
}
