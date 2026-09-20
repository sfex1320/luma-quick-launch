import { HostEventSchema, resultSchemas, StateSchema, type AppState, type HostEvent, type Method, type Methods } from './contracts';
import { initialState } from './data';
interface WebView { postMessage(message: unknown): void; postMessageWithAdditionalObjects?(message: unknown, objects: File[]): void; addEventListener(type: 'message', callback: (event: MessageEvent) => void): void }
declare global { interface Window { chrome?: { webview?: WebView } } }
const webview = window.chrome?.webview;
export const nativeMode = !!webview || new URLSearchParams(location.search).get('mode') === 'native';
type Pending = { method: Method; resolve: (value: unknown) => void; reject: (reason: Error) => void; timeout: ReturnType<typeof setTimeout> };
const pending = new Map<string, Pending>();
const listeners = new Set<(event: HostEvent) => void>();
// Local persistence acknowledgement for read-only derived resources, separate from host state events.
const committedListeners = new Set<() => void>();
export function subscribeCommittedState(listener: () => void) { committedListeners.add(listener); return () => { committedListeners.delete(listener); }; }
function notifyCommittedState() { committedListeners.forEach(listener => listener()); }
webview?.addEventListener('message', ({ data }) => {
  if (!data || typeof data !== 'object' || data.protocol !== 1) return;
  if (data.type === 'event') {
    const parsed = HostEventSchema.safeParse(data);
    if (parsed.success) {
      if (parsed.data.event === 'app.stateChanged') notifyCommittedState();
      listeners.forEach(listener => listener(parsed.data));
    }
    return;
  }
  if (data.type !== 'response' || typeof data.id !== 'string') return;
  const task = pending.get(data.id); if (!task) return;
  clearTimeout(task.timeout); pending.delete(data.id);
  if (data.ok !== true) { task.reject(new Error(typeof data.error?.message === 'string' ? data.error.message : '内核请求失败')); return; }
  const parsed = resultSchemas[task.method].safeParse(data.result);
  if (!parsed.success) task.reject(new Error('内核返回的数据格式不符合协议 v1'));
  else {
    if (task.method === 'app.saveState') notifyCommittedState();
    task.resolve(parsed.data);
  }
});
let demoState = structuredClone(initialState);
export function subscribeHost(listener: (event: HostEvent) => void) { listeners.add(listener); return () => { listeners.delete(listener); }; }
export async function request<M extends Method>(method: M, params: Methods[M]['params'], files?: File[]): Promise<Methods[M]['result']> {
  if (nativeMode) {
    if (!webview) throw new Error('未连接原生内核，请在 WebView2 宿主中打开');
    return new Promise((resolve, reject) => {
      const id = crypto.randomUUID();
      const timeout = setTimeout(() => { pending.delete(id); reject(new Error('内核响应超时，请检查连接')); }, method === 'shell.pickFolder' || method === 'shell.pickFiles' ? 120000 : 8000);
      pending.set(id, { method, resolve: resolve as (value: unknown) => void, reject, timeout });
      try {
        const message = { protocol: 1, type: 'request', id, method, params };
        if (files) {
          if (method !== 'shell.resolveDrop' || !webview.postMessageWithAdditionalObjects) throw new Error('当前内核不支持拖入');
          webview.postMessageWithAdditionalObjects(message, files);
        } else webview.postMessage(message);
      }
      catch { clearTimeout(timeout); pending.delete(id); reject(new Error('无法向内核发送请求')); }
    });
  }
  let result: unknown;
  switch (method) {
    case 'system.getIntegration': case 'system.setAutoStart': case 'system.createDesktopShortcut':
      throw new Error('请在 Luma 桌面程序中管理开机启动和桌面快捷方式。');
    case 'app.getState': {
      const stored = localStorage.getItem('luma.state.v1');
      if (stored) { try { demoState = StateSchema.parse(JSON.parse(stored)); } catch { throw new Error('预览配置损坏。请在设置中恢复默认配置。'); } }
      result = structuredClone(demoState); break;
    }
    case 'app.saveState': {
      const { state, expectedRevision } = params as Methods['app.saveState']['params'];
      if (expectedRevision !== demoState.revision) throw new Error('配置版本冲突，请刷新后重试');
      const next: AppState = StateSchema.parse({ ...state, revision: expectedRevision + 1 });
      localStorage.setItem('luma.state.v1', JSON.stringify(next)); demoState = next; result = structuredClone(next); break;
    }
    case 'shell.openItem': result = { accepted: true }; break;
    case 'shell.getIcon': result = { dataUrl: null }; break;
    case 'folder.getThumbnail': result = { dataUrl: null }; break;
    case 'shell.pickFolder': throw new Error('浏览器无法读取完整 Windows 路径，请手动填写；原生内核接入后可直接选择。');
    case 'shell.pickFiles': case 'shell.resolveDrop': throw new Error('请在 Luma 桌面程序中选择或拖入文件、软件和文件夹。');
    case 'folder.list': case 'folder.open': throw new Error('请在 Luma 桌面程序中浏览真实目录；浏览器预览不读取本机文件。');
    case 'project.detectTest': case 'project.runTest': throw new Error('请在 Luma 桌面程序中识别并运行项目测试。');
    case 'search.query': {
      const { query, scope } = params as Methods['search.query']['params'];
      result = { results: scope === 'settings' || scope === 'content' ? [] : demoState.projects.flatMap(p => p.items.filter(i => `${p.name} ${i.name} ${i.path}`.toLowerCase().includes(query.toLowerCase())).map(i => ({ id: `${p.id}/${i.id}`, title: i.name, subtitle: `${p.name} · ${i.path}`, kind: i.kind, source: 'shortcut' }))).slice(0, 60), indexAvailable: false, note: '浏览器仅预览已保存入口；桌面程序支持 Windows 索引和设置搜索。' }; break;
    }
    case 'search.open': result = { accepted: true }; break;
    case 'window.closeSearch': result = { accepted: true }; break;
    case 'window.sync': result = { applied: true }; break;
    case 'window.openSettings': result = { accepted: true }; break;
  }
  return result as Methods[M]['result'];
}
export function resetDemoStorage() { localStorage.removeItem('luma.state.v1'); location.reload(); }
