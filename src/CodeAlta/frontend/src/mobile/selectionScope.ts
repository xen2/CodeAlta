// The parts of the page Select all keeps to, from the innermost: code, the text of a message (or of a file the viewer
// shows), the body of an open activity, a panel; the timeline holds the messages and the activities.
const parts = "code, pre, .markdown-content, .output-viewer-text, .act-body, .ask-bar, .approval, .sheet-panel, .timeline";

/**
 * Select all, from the menu of a selection on a phone or Ctrl+A, selects the part the selection is in instead of the whole
 * page: the code it is in, else its message, and each Select all after it one part wider, up to the timeline. In the file
 * the viewer shows, it selects that file's text, never the session under it.
 *
 * A text that cannot be edited gets a cancelable `selectstart` on the body before the browser selects the whole page;
 * a selection a finger or a mouse starts gets it on the element under it, while the pointer is down. A selection in no
 * part, such as one in the title, keeps what the browser does, and so does a text field, which keeps Select all to itself.
 */
export function installSelectionScope(window: Window & typeof globalThis) {
  const document = window.document;
  let pressed = false;
  window.addEventListener("pointerdown", () => { pressed = true; }, true);
  for (const type of ["pointerup", "pointercancel"]) window.addEventListener(type, () => { pressed = false; }, true);

  document.addEventListener("selectstart", event => {
    if (event.target !== document.body || pressed) return;
    const selection = window.getSelection();
    const part = selection ? partToSelect(selection) : null;
    if (!part) return;
    event.preventDefault();
    selection!.selectAllChildren(part);
  }, true);

  // The parts that hold a node, the innermost first; a part with the same text as the one in it is the same for the
  // reader (a code block and its code, a message that is only a code block), and is left out.
  function partsOf(node: Node): Element[] {
    const found: Element[] = [];
    for (let element = node instanceof window.Element ? node : node.parentElement; element; element = element.parentElement) {
      if (element.matches(parts) && found.at(-1)?.textContent !== element.textContent) found.push(element);
    }
    return found;
  }

  // The innermost part that holds the selection and that it does not cover yet: each Select all goes one part wider.
  // The widest part stays selected. A file shown full screen covers the page: a selection in its bar or under it selects its text.
  function partToSelect(selection: Selection): Element | null {
    const range = selection.rangeCount ? selection.getRangeAt(0) : null;
    const viewer = document.querySelector(".output-viewer");
    const held = range && (!viewer || viewer.contains(range.commonAncestorContainer)) ? partsOf(range.commonAncestorContainer) : [];
    if (held.length) return held.find(part => !covers(range!, part)) ?? held.at(-1)!;
    return viewer?.querySelector(".output-viewer-text, .output-viewer-drawn") ?? null;
  }

  // Whether a range holds all the text of a part, from its first character to its last: a phone may move the ends of a
  // selection to the text it shows, past the spaces and the line ends around it.
  function covers(range: Range, part: Element): boolean {
    const walker = document.createTreeWalker(part, window.NodeFilter.SHOW_TEXT, { acceptNode: node => /\S/u.test((node as Text).data) ? window.NodeFilter.FILTER_ACCEPT : window.NodeFilter.FILTER_SKIP });
    const first = walker.nextNode() as Text | null;
    if (!first) return true;
    let last = first;
    for (let node = walker.nextNode() as Text | null; node; node = walker.nextNode() as Text | null) last = node;
    return range.comparePoint(first, first.data.length - first.data.trimStart().length) === 0
      && range.comparePoint(last, last.data.trimEnd().length) === 0;
  }
}
