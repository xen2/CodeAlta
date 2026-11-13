import { copyText } from "./clipboard";

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
 *
 * A phone hides its handles and its Copy menu for a selection a script sets, until a tap on it: a bar shows just above
 * the selection, with Copy, which copies its text, and a button that selects the next part, as the next Select all does,
 * named for that part. It stays while the selection does, and goes when the selection changes or goes, or at a tap
 * elsewhere, the selection included: the phone's own menu then shows. `note` says that the copy failed.
 */
export function installSelectionScope(window: Window & typeof globalThis, note: (text: string) => void) {
  const document = window.document;
  let pressed = false;
  window.addEventListener("pointerdown", event => {
    pressed = true;
    if (!bar.hidden && !bar.contains(event.target as Node)) hideBar();
  }, true);
  for (const type of ["pointerup", "pointercancel"]) window.addEventListener(type, () => { pressed = false; }, true);

  document.addEventListener("selectstart", event => {
    if (event.target !== document.body || pressed) return;
    const selection = window.getSelection();
    const part = selection ? partToSelect(selection) : null;
    if (!part) return;
    event.preventDefault();
    selectPart(selection!, part);
  }, true);

  function selectPart(selection: Selection, part: Element) {
    selection.selectAllChildren(part);
    showBar(selection, part);
  }

  // ---- The bar of a selection Select all made: Copy, and the next Select all ----
  const bar = document.createElement("div");
  bar.className = "select-bar";
  bar.hidden = true;
  const copy = document.createElement("button"), wider = document.createElement("button");
  copy.type = wider.type = "button";
  copy.className = "select-copy";
  copy.setAttribute("aria-live", "polite");
  wider.className = "select-wider";
  bar.append(copy, wider);
  // The text Copy copies and the part the other button selects: a tap on the bar may clear the selection before its click.
  let copied = "", next: Element | null = null, timer = 0, frame = 0;

  function showBar(selection: Selection, part: Element) {
    hideBar();
    const text = selection.toString();
    if (!text.trim()) return;
    copied = text;
    const outer = partToSelect(selection);
    next = outer !== part ? outer : null;
    copy.textContent = "Copy";
    showWider();
    bar.hidden = false;
    if (bar.parentElement !== document.body) document.body.append(bar);
    placeBar();
  }

  function showWider() {
    wider.hidden = !next;
    if (!next) return;
    wider.textContent = next.matches(".timeline") ? "Select all messages"
      : next.matches(".act-body") ? "Select work"
      : next.closest(".output-viewer") ? "Select file"
      : next.matches(".markdown-content") ? "Select message"
      : "Select more";
  }

  function hideBar() {
    window.clearTimeout(timer);
    window.cancelAnimationFrame(frame);
    frame = 0;
    bar.hidden = true;
    delete copy.dataset.state;
    copied = "";
    next = null;
  }

  // Just above the selection and centered on it, inside the screen and under the bar at its top.
  function placeBar() {
    frame = 0;
    const selection = window.getSelection();
    if (bar.hidden || !selection?.rangeCount) return;
    const box = selection.getRangeAt(0).getBoundingClientRect();
    const top = document.querySelector(".output-viewer .output-viewer-bar") ?? document.getElementById("bar");
    const below = Math.max(top?.getBoundingClientRect().bottom ?? 0, 0) + 8;
    const width = bar.offsetWidth, height = bar.offsetHeight;
    const left = Math.min(Math.max(box.left + box.width / 2 - width / 2, 8), document.documentElement.clientWidth - width - 8);
    bar.style.left = `${left}px`;
    bar.style.top = `${Math.max(Math.min(box.top - height - 8, window.innerHeight - height - 8), below)}px`;
  }
  const followSelection = () => { if (!bar.hidden && !frame) frame = window.requestAnimationFrame(placeBar); };
  window.addEventListener("scroll", followSelection, { capture: true, passive: true });
  window.addEventListener("resize", followSelection);

  document.addEventListener("selectionchange", () => {
    if (bar.hidden || copy.dataset.state === "copying") return;
    const selection = window.getSelection();
    if (!selection || selection.isCollapsed || selection.toString() !== copied) hideBar();
    else followSelection();
  });

  // A press on the bar keeps the selection: the browser would clear it, and with it the bar, before the click.
  bar.addEventListener("pointerdown", event => event.preventDefault());
  bar.addEventListener("mousedown", event => event.preventDefault());
  copy.addEventListener("click", async () => {
    if (copy.dataset.state === "copying") return;
    window.clearTimeout(timer);
    copy.dataset.state = "copying";
    try {
      await copyText(window, copied);
      copy.dataset.state = "copied";
      copy.textContent = "Copied";
      timer = window.setTimeout(() => { delete copy.dataset.state; copy.textContent = "Copy"; }, 1200);
    } catch (error) {
      delete copy.dataset.state;
      note(`Not copied: ${error instanceof Error ? error.message : String(error)}`);
    }
  });
  wider.addEventListener("click", () => {
    const selection = window.getSelection();
    if (next && selection) selectPart(selection, next);
  });

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
