import type { AppState, Preferences } from './contracts';
export const defaults: Preferences = { width: 640, height: 88, iconSize: 40, radius: 22, material: 'frost', theme: 'light', reducedMotion: false, autoHide: true };
export const initialState: AppState = {
  schemaVersion: 1, revision: 0, preferences: defaults,
  projects: [
    { id: 'atelier', name: '品牌设计', description: '让灵感，成为看得见的作品。', color: 'mint', pinned: true, items: [
      { id: 'root', name: '品牌设计', path: 'D:\\Projects\\Atelier', kind: 'folder' },
      { id: 'references', name: '灵感与参考', path: 'D:\\Projects\\Atelier\\References', kind: 'folder' },
      { id: 'design', name: '设计源文件', path: 'D:\\Projects\\Atelier\\Design', kind: 'folder' },
      { id: 'assets', name: '品牌素材', path: 'D:\\Projects\\Atelier\\Assets', kind: 'folder' },
      { id: 'delivery', name: '最终交付', path: 'D:\\Projects\\Atelier\\Delivery', kind: 'folder' },
    ] },
    { id: 'website', name: '个人网站', description: '自己的数字花园，持续生长。', color: 'blue', pinned: true, items: [
      { id: 'root', name: '个人网站', path: 'D:\\Projects\\Portfolio', kind: 'folder' },
      { id: 'src', name: '前端代码', path: 'D:\\Projects\\Portfolio\\src', kind: 'folder' },
      { id: 'public', name: '网站资源', path: 'D:\\Projects\\Portfolio\\public', kind: 'folder' },
    ] },
    { id: 'film', name: '影像计划', description: '收集光影，记录每一个瞬间。', color: 'violet', pinned: true, items: [
      { id: 'root', name: '影像计划', path: 'D:\\Projects\\Film', kind: 'folder' },
      { id: 'footage', name: '原始素材', path: 'D:\\Projects\\Film\\Footage', kind: 'folder' },
      { id: 'export', name: '影片导出', path: 'D:\\Projects\\Film\\Export', kind: 'folder' },
      { id: 'audio', name: '音乐与声音', path: 'D:\\Projects\\Film\\Audio', kind: 'folder' },
    ] },
    { id: 'daily', name: '日常工作', description: '常用的东西，就放在手边。', color: 'peach', pinned: true, items: [
      { id: 'root', name: '日常工作', path: 'D:\\Workspace', kind: 'folder' },
      { id: 'docs', name: '工作文档', path: 'D:\\Workspace\\Documents', kind: 'folder' },
    ] },
    { id: 'inspiration', name: '灵感收集', description: '先存下来，好想法总会用上。', color: 'gold', pinned: true, items: [
      { id: 'root', name: '灵感收集', path: 'D:\\Inspiration', kind: 'folder' },
      { id: 'images', name: '图片收藏', path: 'D:\\Inspiration\\Images', kind: 'folder' },
    ] },
  ],
};
