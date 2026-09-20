import { z } from 'zod';

export const ColorSchema = z.enum(['mint', 'blue', 'violet', 'peach', 'gold']);
export const LaunchCommandSchema = z.object({ command: z.string().min(1).max(1000).refine(value => value.trim().length > 0 && !/[\r\n\0]/.test(value), '请输入非空的单行 CMD 命令'), workingDirectory: z.string().min(1).max(4096).refine(value => !/[\r\n\0]/.test(value) && /^(?:[a-zA-Z]:[\\/]|\\\\[^\\]+\\[^\\]+)/.test(value), '请输入有效的绝对工作目录') });
export const ItemSchema = z.object({ id: z.string().min(1), name: z.string().min(1).max(120), path: z.string().min(1).max(4096), kind: z.enum(['folder', 'file', 'app']), launch: LaunchCommandSchema.optional() });
export const ProjectSchema = z.object({ id: z.string().min(1), name: z.string().min(1).max(60), description: z.string().max(160), color: ColorSchema, pinned: z.boolean(), items: z.array(ItemSchema).min(1).max(200) });
export const PreferencesSchema = z.object({ width: z.number().min(320).max(1120), height: z.number().min(64).max(160), iconSize: z.number().min(24).max(64), radius: z.number().min(12).max(32), material: z.enum(['frost', 'soft', 'solid']), theme: z.enum(['light', 'dark']), reducedMotion: z.boolean(), autoHide: z.boolean() });
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
export const ImportedShortcutSchema = ItemSchema.omit({ id: true });
export type ImportedShortcut = z.infer<typeof ImportedShortcutSchema>;
export const SearchScopeSchema = z.enum(['all', 'shortcuts', 'files', 'content', 'settings']);
export const SearchResultSchema = z.object({ id: z.string().min(1), title: z.string(), subtitle: z.string(), kind: z.enum(['folder', 'file', 'app', 'setting']), source: z.enum(['shortcut', 'index', 'settings']) });
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
export interface Methods {
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
  'search.query': { params: { query: string; scope: SearchScope }; result: z.infer<typeof SearchResponseSchema> };
  'search.open': { params: { resultId: string }; result: { accepted: boolean } };
  'window.closeSearch': { params: Record<string, never>; result: { accepted: boolean } };
  'window.sync': { params: { expanded: boolean; rects: Rect[]; interacting?: boolean; visibilityId?: number }; result: { applied: boolean } };
  'window.openSettings': { params: { section: 'projects' | 'appearance' | 'search' }; result: { accepted: boolean } };
}
export type Method = keyof Methods;
export type HostEvent = { event: 'app.stateChanged'; data: AppState } | { event: 'window.visibility'; data: { visible: boolean; visibilityId?: number } };
export const resultSchemas = {
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
};
export const HostEventSchema = z.discriminatedUnion('event', [z.object({ event: z.literal('app.stateChanged'), data: StateSchema }), z.object({ event: z.literal('window.visibility'), data: z.object({ visible: z.boolean(), visibilityId: z.number().int().nonnegative().max(Number.MAX_SAFE_INTEGER).optional() }) })]);
