export const PROJECT_DRAG_TYPE = 'application/x-luma-project';
export const hasExternalFiles = (data: DataTransfer) => Array.from(data.types).includes('Files');
export const hasProjectDrag = (data: DataTransfer) => !hasExternalFiles(data) && Array.from(data.types).includes(PROJECT_DRAG_TYPE);
