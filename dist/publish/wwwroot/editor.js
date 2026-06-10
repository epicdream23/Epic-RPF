// Thin wrapper around Monaco (the editor that powers VS Code). One editor
// instance is reused across edit tabs; each tab keeps its own model so edits,
// undo history and scroll position survive tab switches.

let editor = null;
let monaco = null;
let readyP = null;

export function initEditor(container) {
  readyP = (async () => {
    monaco = await window.monacoReady;
    // Colour even very large files. Monaco disables tokenization past ~20 MB /
    // 300k lines (so a 3M-line XML shows as plain white). isTooLargeForTokenization()
    // is the SOLE gate (the _isTooLargeForTokenization field is read nowhere else),
    // so overriding it on the TextModel prototype re-enables syntax highlighting for
    // huge files. Slower on 3M lines, but the user wants the colour.
    try {
      const probe = monaco.editor.createModel('', 'plaintext');
      const proto = Object.getPrototypeOf(probe);
      if (proto && typeof proto.isTooLargeForTokenization === 'function') proto.isTooLargeForTokenization = () => false;
      probe.dispose();
    } catch { /* non-fatal: fall back to Monaco's default large-file behaviour */ }
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
      maxTokenizationLineLength: 50000,   // colour long lines too (CW arrays etc.)
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
