// Thin wrapper around Monaco (the editor that powers VS Code). One editor
// instance is reused across edit tabs; each tab keeps its own model so edits,
// undo history and scroll position survive tab switches.

let editor = null;
let monaco = null;
let readyP = null;

export function initEditor(container) {
  readyP = (async () => {
    monaco = await window.monacoReady;
    editor = monaco.editor.create(container, {
      value: '',
      language: 'plaintext',
      theme: 'vs-dark',
      automaticLayout: true,
      fontSize: 13,
      minimap: { enabled: true },
      scrollBeyondLastLine: false,
      renderWhitespace: 'selection',
      tabSize: 2,
      wordWrap: 'off',
    });
  })();
  return readyP;
}

export async function showTab(tab, onChange) {
  await readyP;
  if (!tab.model) {
    tab.model = monaco.editor.createModel(tab.data.content || '', tab.data.language || 'plaintext');
    tab.original = tab.data.content || '';
    tab.model.onDidChangeContent(() => onChange && onChange(tab));
  }
  editor.setModel(tab.model);
  setTimeout(() => editor && editor.layout(), 0);
}

export function value(tab) {
  return tab.model ? tab.model.getValue() : (tab.data.content || '');
}
export function dirty(tab) {
  return !!tab.model && tab.model.getValue() !== tab.original;
}
export function markSaved(tab) {
  if (tab.model) tab.original = tab.model.getValue();
}
export function disposeTab(tab) {
  if (tab.model) { tab.model.dispose(); tab.model = null; }
}
export function layout() { if (editor) editor.layout(); }
