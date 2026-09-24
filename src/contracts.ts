import { isWebsiteUrl, isWebsiteIcon } from './core/websiteValidation.ts';
import { z } from 'zod';
import { chordId, contextActions, reservedChord, validHotkeyCode } from './core/hotkeys.ts';

export const ShortcutBindingSchema = z.object({
  id: z.string().min(1).max(128).refine(value => !!value.trim(), '快捷键ID不能为空白'), code: z.string().refine(validHotkeyCode, '请选择有效的非修饰按键'),
  ctrl: z.boolean(), shift: z.boolean(), alt: z.boolean(), scope: z.enum(['panel', 'global']),
  action: z.enum(['dock','search','settings','edit','project','item','openDirectory','newFolder','copyAddress','runProject']),
  projectId: z.string().min(1).max(200).refine(value => !!value.trim(), '项目ID不能为空白').optional(), itemId: z.string().min(1).max(200).refine(value => !!value.trim(), '入口ID不能为空白').optional(),
}).superRefine((binding, ctx) => {
  const fail = (message: string) => ctx.addIssue({ code: 'custom', message });
  if (binding.scope === 'global' && !(binding.ctrl || binding.shift || binding.alt)) fail('单键只可在面板内使用');
  if (reservedChord(binding)) fail('Ctrl + K 和 Ctrl + Alt + 空格已用于搜索');
  if (binding.scope === 'global' && (contextActions as readonly string[]).includes(binding.action)) fail('当前目录操作只能在面板内使用');
  if (binding.action === 'project' || binding.action === 'item') {
    if (!binding.projectId || binding.action === 'item' && !binding.itemId) fail('请选择已保存的项目和入口');
    if (binding.action === 'project' && binding.itemId) fail('项目快捷键不接受入口ID');
  } else if (binding.projectId || binding.itemId) fail('功能快捷键不接受项目或入口ID');
});
export type ShortcutBinding = z.infer<typeof ShortcutBindingSchema>;
export const ShortcutListSchema = z.array(ShortcutBindingSchema).max(128).superRefine((bindings, ctx) => {
  if (new Set(bindings.map(binding => binding.id)).size !== bindings.length) ctx.addIssue({ code: 'custom', message: '快捷键ID重复' });
  if (new Set(bindings.map(chordId)).size !== bindings.length) ctx.addIssue({ code: 'custom', message: '该按键组合已分配，请先移除原绑定' });
});

export const ColorSchema = z.enum(['mint', 'blue', 'violet', 'peach', 'gold']);
export const LaunchCommandSchema = z.object({ command: z.string().min(1).max(1000).refine(value => value.trim().length > 0 && !/[\r\n\0]/.test(value), '请输入非空的单行 CMD 命令'), workingDirectory: z.string().min(1).max(4096).refine(value => !/[\r\n\0]/.test(value) && /^(?:[a-zA-Z]:[\\/]|\\\\[^\\]+\\[^\\]+)/.test(value), '请输入有效的绝对工作目录') });
export const ItemSchema = z.object({ id: z.string().min(1), name: z.string().min(1).max(120), path: z.string().min(1).max(4096), kind: z.enum(['folder', 'file', 'app', 'url']), launch: LaunchCommandSchema.optional(), note: z.string().max(240).optional(), websiteIcon: z.string().max(87406).refine(isWebsiteIcon, '网站图标必须为小尺寸静态 PNG').optional() }).refine(item => item.kind !== 'url' || isWebsiteUrl(item.path), '网址必须是有效的 HTTP(S) 地址').refine(item => !item.websiteIcon || item.kind === 'url', '只有网址入口可保存网站图标');
export const ProjectSchema = z.object({ id: z.string().min(1), name: z.string().min(1).max(60), description: z.string().max(160), color: ColorSchema, pinned: z.boolean(), favorite: z.boolean().optional(), items: z.array(ItemSchema).min(1).max(200) });
export const PreferencesSchema = z.object({ width: z.number().min(320).max(1120), height: z.number().min(64).max(160), iconSize: z.number().min(24).max(64), radius: z.number().min(12).max(32), material: z.enum(['frost', 'soft', 'solid']), theme: z.enum(['light', 'dark']), reducedMotion: z.boolean(), autoHide: z.boolean(), recentLimit: z.number().int().min(6).max(10).optional(), shortcuts: ShortcutListSchema.optional(), motionSpeed: z.enum(['relaxed', 'standard', 'brisk']).optional() });
export const StateSchema = z.object({ schemaVersion: z.literal(1), revision: z.number().int().nonnegative(), projects: z.array(ProjectSchema).max(100), preferences: PreferencesSchema }).superRefine((state, ctx) => {
  const ids = state.projects.map(p => p.id);
  if (new Set(ids).size !== ids.length) ctx.addIssue({ code: 'custom', message: 'Duplicate project ID' });
  for (const p of state.projects) if (new Set(p.items.map(i => i.id)).size !== p.items.length) ctx.addIssue({ code: 'custom', message: 'Duplicate item ID' });
  for (const p of state.projects) for (const item of p.items) if (item.launch && item.kind !== 'folder') ctx.addIssue({ code: 'custom', message: '启动命令只能绑定文件夹' });
});
export type AppState = z.infer<typeof StateSchema>;
export type Project = z.infer<typeof ProjectSchema>;
export type LaunchItem = z.infer<typeof ItemSchema>;
export type Preferences = z.infer<typeof PreferencesSchema>;
export type Color = z.infer<typeof ColorSchema>;
export interface Rect { x: number; y: number; width: number; height: number }
export const ImportedShortcutSchema = z.object({ id: z.string().optional(), name: z.string().min(1).max(120), path: z.string().min(1).max(4096), kind: z.enum(['folder','file','app','url']), launch: LaunchCommandSchema.optional(), note: z.string().max(240).optional(), websiteIcon: z.string().max(90000).optional() }).omit({ id: true });
export type ImportedShortcut = z.infer<typeof ImportedShortcutSchema>;
export const SearchScopeSchema = z.enum(['all', 'shortcuts', 'files', 'content', 'settings']);
export const SearchResultSchema = z.object({ id: z.string().min(1), title: z.string(), subtitle: z.string(), kind: z.enum(['folder', 'file', 'app', 'url', 'setting']), source: z.enum(['shortcut', 'index', 'settings']) });
export const SearchResponseSchema = z.object({ results: z.array(SearchResultSchema).max(60), indexAvailable: z.boolean(), note: z.string() });
export type SearchScope = z.infer<typeof SearchScopeSchema>;
export type SearchResult = z.infer<typeof SearchResultSchema>;
export const FolderEntrySchema = z.object({ id: z.string().min(1), name: z.string(), kind: z.enum(['folder', 'file', 'app']) });
export const FolderListingSchema = z.object({ folderId: z.string().min(1), name: z.string(), entries: z.array(FolderEntrySchema).max(200), parentId: z.string().nullable(), truncated: z.boolean() });
export type FolderEntry = z.infer<typeof FolderEntrySchema>;
export type FolderListing = z.infer<typeof FolderListingSchema>;
export const ProjectTestTaskSchema = z.object({ id: z.string().min(1).max(200), label: z.string().min(1).max(120), command: z.string().min(1).max(1000) });
export type ProjectTestTask = z.infer<typeof ProjectTestTaskSchema>;
export const IconResponseSchema = z.object({ dataUrl: z.string().max(350000).refine(value => {
  if (!/^data:image\/png;base64,[A-Za-z0-9+/]+={0,2}$/.test(value)) return false;
  try {
    const data = atob(value.slice(22));
    return data.length >= 33 && data.startsWith('\x89PNG\r\n\x1a\n') && data.slice(12, 16) === 'IHDR';
  } catch { return false; }
}).nullable() });
export const IntegrationSchema = z.object({ autoStart: z.boolean(), autoStartHere: z.boolean(), desktopShortcut: z.boolean() });
export type IntegrationStatus = z.infer<typeof IntegrationSchema>;
export const RecentSchema = z.object({ entries: z.array(z.object({ id: z.string(), name: z.string(), path: z.string(), kind: z.enum(['file','folder']) })).max(10), note: z.string() });
export const MutationSchema = z.object({ changedCount: z.number(), completed: z.boolean(), errorCode: z.string().nullable(), message: z.string().nullable() });
export interface Methods {
  'shell.getAppCapabilities': { params: { projectId: string; itemId: string }; result: { recentSupported: boolean } };
  'folder.transfer': { params: { operation: 'copy' | 'move' | 'link'; targetProject: string; targetItem: string; targetFolderId: string; sourceProject?: string; sourceItem?: string; sourceEntryId?: string }; result: z.infer<typeof MutationSchema> };
  'shortcut.getStatus': { params: Record<string, never>; result: { bindings: { id: string; registered: boolean; message: string }[] } };
  'shortcut.execute': { params: { id: string }; result: { accepted: boolean } };
  'shortcut.setRecording': { params: { active: boolean }; result: { accepted: boolean } };
  'website.inspect': { params: { url: string }; result: { url: string; title: string; dataUrl: string | null } };
  'shell.getRecent': { params: { projectId: string; itemId: string; limit: number }; result: z.infer<typeof RecentSchema> };
  'shell.openRecent': { params: { projectId: string; itemId: string; entryId: string }; result: { accepted: boolean } };
  'folder.getPath': { params: { projectId: string; itemId: string; entryId: string }; result: { path: string } };
  'folder.createFolder': { params: { projectId: string; itemId: string; folderId: string; name: string }; result: z.infer<typeof MutationSchema> };
  'folder.rename': { params: { projectId: string; itemId: string; entryId: string; name: string }; result: z.infer<typeof MutationSchema> };
  'folder.move': { params: { sourceProject: string; sourceItem: string; sourceEntryIds: string[]; targetProject: string; targetItem: string; targetFolderId: string }; result: z.infer<typeof MutationSchema> };
  'system.getIntegration': { params: Record<string, never>; result: IntegrationStatus };
  'system.setAutoStart': { params: { enabled: boolean }; result: IntegrationStatus };
  'system.createDesktopShortcut': { params: Record<string, never>; result: IntegrationStatus };
  'app.getState': { params: Record<string, never>; result: AppState };
  'app.saveState': { params: { state: AppState; expectedRevision: number }; result: AppState };
  'shell.openItem': { params: { projectId: string; itemId: string }; result: { accepted: boolean } };
  'shell.getIcon': { params: { projectId: string; itemId: string; size?: 32 | 48 | 64 | 96 }; result: z.infer<typeof IconResponseSchema> };
  'shell.pickFolder': { params: Record<string, never>; result: { path: string; name: string } | null };
  'shell.pickFiles': { params: Record<string, never>; result: ImportedShortcut[] };
  'shell.resolveDrop': { params: Record<string, never>; result: ImportedShortcut[] };
  'folder.list': { params: { projectId: string; itemId: string; folderId?: string }; result: FolderListing };
  'folder.open': { params: { projectId: string; itemId: string; entryId: string }; result: { accepted: boolean } };
  'folder.getThumbnail': { params: { projectId: string; itemId: string; entryId: string; size?: 64 | 96 | 128 }; result: z.infer<typeof IconResponseSchema> };
  'project.detectTest': { params: { projectId: string; itemId: string; folderId: string }; result: { task: ProjectTestTask | null } };
  'project.runTest': { params: { projectId: string; itemId: string; taskId: string }; result: { opened: boolean } };
  'search.query': { params: { query: string; scope: SearchScope; appAliases?: boolean; fuzzyNames?: boolean }; result: z.infer<typeof SearchResponseSchema> };
  'search.open': { params: { resultId: string }; result: { accepted: boolean } };
  'window.closeSearch': { params: Record<string, never>; result: { accepted: boolean } };
  'window.sync': { params: { expanded: boolean; rects: Rect[]; interacting?: boolean; visibilityId?: number }; result: { applied: boolean } };
  'window.openSettings': { params: { section: 'projects' | 'appearance' | 'search' }; result: { accepted: boolean } };
  'update.check': { params: Record<string, never>; result: UpdateCheckResult };
  'update.download': { params: { url: string; fileName: string; sha256Url?: string }; result: { path: string; verified: boolean; bytes: number } };
  'update.apply': { params: { path: string }; result: { accepted: boolean; mode: 'portable' | 'installer' } };
}
export interface UpdateCheckResult { currentVersion: string; latestVersion: string; hasUpdate: boolean; notes: string; publishedAt: string; installMode: 'portable' | 'installer'; asset: { name: string; url: string; size: number; sha256Url: string | null } | null }
export type Method = keyof Methods;
export type HostEvent = { event: 'app.stateChanged'; data: AppState } | { event: 'window.visibility'; data: { visible: boolean; visibilityId?: number } } | { event: 'shortcut.activated'; data: { id: string; serial: number } };
export const resultSchemas = {
  'shell.getAppCapabilities': z.object({ recentSupported: z.boolean() }),
  'folder.transfer': MutationSchema,
  'shortcut.getStatus': z.object({ bindings: z.array(z.object({ id: z.string(), registered: z.boolean(), message: z.string() })).max(128) }),
  'shortcut.execute': z.object({ accepted: z.boolean() }),
  'shortcut.setRecording': z.object({ accepted: z.boolean() }),
  'website.inspect': z.object({ url: z.string(), title: z.string(), dataUrl: IconResponseSchema.shape.dataUrl }),
  'shell.getRecent': RecentSchema,
  'shell.openRecent': z.object({ accepted: z.boolean() }),
  'folder.getPath': z.object({ path: z.string() }),
  'folder.createFolder': MutationSchema,
  'folder.rename': MutationSchema,
  'folder.move': MutationSchema,
  'system.getIntegration': IntegrationSchema,
  'system.setAutoStart': IntegrationSchema,
  'system.createDesktopShortcut': IntegrationSchema,
  'app.getState': StateSchema,
  'app.saveState': StateSchema,
  'shell.openItem': z.object({ accepted: z.boolean() }),
  'shell.getIcon': IconResponseSchema,
  'shell.pickFolder': z.object({ path: z.string().min(1), name: z.string().min(1) }).nullable(),
  'shell.pickFiles': z.array(ImportedShortcutSchema).max(100),
  'shell.resolveDrop': z.array(ImportedShortcutSchema).max(100),
  'folder.list': FolderListingSchema,
  'folder.open': z.object({ accepted: z.boolean() }),
  'folder.getThumbnail': IconResponseSchema,
  'project.detectTest': z.object({ task: ProjectTestTaskSchema.nullable() }),
  'project.runTest': z.object({ opened: z.boolean() }),
  'search.query': SearchResponseSchema,
  'search.open': z.object({ accepted: z.boolean() }),
  'window.closeSearch': z.object({ accepted: z.boolean() }),
  'window.sync': z.object({ applied: z.boolean() }),
  'window.openSettings': z.object({ accepted: z.boolean() }),
  'update.check': z.object({ currentVersion: z.string(), latestVersion: z.string(), hasUpdate: z.boolean(), notes: z.string(), publishedAt: z.string(), installMode: z.enum(['portable', 'installer']), asset: z.object({ name: z.string(), url: z.string(), size: z.number(), sha256Url: z.string().nullable() }).nullable() }),
  'update.download': z.object({ path: z.string(), verified: z.boolean(), bytes: z.number() }),
  'update.apply': z.object({ accepted: z.boolean(), mode: z.enum(['portable', 'installer']) }),
};
export const HostEventSchema = z.discriminatedUnion('event', [z.object({ event: z.literal('app.stateChanged'), data: StateSchema }), z.object({ event: z.literal('window.visibility'), data: z.object({ visible: z.boolean(), visibilityId: z.number().int().nonnegative().max(Number.MAX_SAFE_INTEGER).optional() }) }), z.object({ event: z.literal('shortcut.activated'), data: z.object({ id: z.string(), serial: z.number().int().nonnegative().max(Number.MAX_SAFE_INTEGER) }) })]);
