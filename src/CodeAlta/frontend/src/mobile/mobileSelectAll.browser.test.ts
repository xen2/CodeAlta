import assert from "node:assert/strict";
import test from "node:test";
import { browserExecutable } from "../browserTarget";
import type { TimelineEvent } from "./contract";
import { session } from "./fixtureServer";
import { openPhone } from "./phoneBrowser";
import { testLimit } from "../testTime";

test("Select all on the phone selects the code, then the message, then the timeline, and in a file only its text", { skip: !browserExecutable, timeout: testLimit(120_000) }, async () => {
  const now = new Date().toISOString();
  const events: TimelineEvent[] = [];
  let seq = 0;
  const event = (kind: string, text: string, extra: Partial<TimelineEvent> = {}) => { const item = { seq: ++seq, at: now, kind, text, activityId: null, ...extra }; events.push(item); return item; };
  event("user.message", "Build the parser and say what fails.");
  event("assistant.reasoning", "Looking at the build first.");
  event("activity.toolcall.started", "shell", { activityId: "call-1", subject: "dotnet build" });
  event("activity.toolcall.completed", "shell", { activityId: "call-1", subject: "dotnet build" });
  const answer = event("assistant.message", "Run `npm test` before you commit.\n\n```sh\ndotnet build -c Release\n```\n\nThen read [the notes](docs/notes.md).");
  const phone = await openPhone(server => {
    server.state.sessions = [session("s1", "Build the parser", "p1", 0)];
    server.state.events.set("s1", events);
    server.state.linkedFiles.set(`s1/${answer.seq}/docs/notes.md`, "# Notes\n\nUse `alta job start` for long runs.\n\nThe rest of the notes.");
  }, "");
  const { evaluate, waitFor, click, command } = phone;
  // Selects the text from a word to another (or the word alone), as a long press and the handles do.
  const select = (root: string, from: string, to = from) => evaluate<boolean>(`(() => {
    const walker = document.createTreeWalker(document.querySelector(${JSON.stringify(root)}), NodeFilter.SHOW_TEXT), at = word => {
      for (let node = walker.nextNode(); node; node = walker.nextNode()) { const index = node.data.indexOf(word); if (index >= 0) return [node, index]; }
      return null;
    };
    const start = at(${JSON.stringify(from)}), end = ${JSON.stringify(from === to)} ? start : at(${JSON.stringify(to)});
    if (!start || !end) return false;
    getSelection().setBaseAndExtent(start[0], start[1], end[0], end[1] + ${JSON.stringify(to)}.length);
    return true;
  })()`);
  // Select all as the menu of a selection or Ctrl+A run it, then the part the selection holds whole and its text.
  const selectAll = async () => { await evaluate('document.execCommand("selectAll")'); return selected(); };
  const selected = () => evaluate<{ part: string; text: string }>(`(() => {
    const range = getSelection().getRangeAt(0), part = range.commonAncestorContainer;
    const name = part.nodeType === Node.ELEMENT_NODE ? part.tagName.toLowerCase() + (part.id ? "#" + part.id : "") + (part.className ? "." + part.className.split(" ").join(".") : "") : "text";
    return { part: name, text: getSelection().toString().trim() };
  })()`);
  const message = "li.msg-assistant .markdown-content";
  try {
    await phone.signIn();
    await click(".row[data-id=s1] .card-link");
    await waitFor(`document.querySelector(${JSON.stringify(`${message} pre code`)})`, "the answer");

    // ---- Inline code: its text, then the message without its label, then the timeline; the timeline stays ----
    assert.ok(await select(message, "test"));
    assert.deepEqual(await selectAll(), { part: "code", text: "npm test" });
    let step = await selectAll();
    assert.equal(step.part, "div.markdown-content");
    assert.ok(step.text.startsWith("Run npm test before you commit.") && step.text.endsWith("Then read the notes."), step.text);
    assert.ok(!step.text.includes("Agent"), "the label of the message is left out");
    step = await selectAll();
    assert.equal(step.part, "ol#timeline.timeline");
    assert.ok(step.text.includes("Build the parser and say what fails.") && step.text.includes("Then read the notes."), step.text);
    assert.equal((await selectAll()).part, "ol#timeline.timeline", "nothing wider than the timeline");

    // ---- A code block: its code, without the language or the copy button ----
    assert.ok(await select(message, "Release"));
    step = await selectAll();
    assert.deepEqual([step.part.split(".")[0], step.text], ["code", "dotnet build -c Release"]);
    assert.equal((await selectAll()).part, "div.markdown-content");

    // ---- Plain text, and a selection that runs out of the code: the message ----
    assert.ok(await select(message, "commit"));
    assert.equal((await selectAll()).part, "div.markdown-content");
    assert.ok(await select(message, "npm", "commit"));
    assert.equal((await selectAll()).part, "div.markdown-content");

    // ---- The bar: above the selection, Copy copies its text and the other button selects the next part ----
    // A browser without a person refuses the clipboard: this one holds what is written.
    await evaluate('Object.defineProperty(navigator, "clipboard", { configurable: true, value: { writeText: async text => { window.copied = text; } } })');
    await command("Emulation.setTouchEmulationEnabled", { enabled: true, maxTouchPoints: 5 });
    // A tap of a finger on the middle of an element, with the touch and pointer events of a phone.
    const tap = async (selector: string) => {
      const [x, y] = await evaluate<number[]>(`(() => { const box = document.querySelector(${JSON.stringify(selector)}).getBoundingClientRect(); return [box.x + box.width / 2, box.y + box.height / 2]; })()`);
      await command("Input.dispatchTouchEvent", { type: "touchStart", touchPoints: [{ x, y }] });
      await command("Input.dispatchTouchEvent", { type: "touchEnd", touchPoints: [] });
    };
    const copy = 'document.querySelector(".select-copy")';
    // Whether the bar shows, and the name of its other button, or null without it.
    const bar = () => evaluate<{ shown: boolean; wider: string | null }>(`(() => {
      const bar = document.querySelector(".select-bar"), wider = bar?.querySelector(".select-wider");
      return { shown: !!bar && !bar.hidden, wider: wider && !wider.hidden ? wider.textContent : null };
    })()`);
    assert.ok(await select(message, "test"));
    await selectAll();
    assert.deepEqual(await bar(), { shown: true, wider: "Select message" }, "a bar for the selection Select all made");
    const place = await evaluate<{ bar: number[]; selection: number[]; top: number }>(`(() => {
      const box = document.querySelector(".select-bar").getBoundingClientRect(), selected = getSelection().getRangeAt(0).getBoundingClientRect();
      return { bar: [box.left, box.top, box.right, box.bottom], selection: [selected.left, selected.top, selected.right], top: document.getElementById("bar").getBoundingClientRect().bottom };
    })()`);
    assert.ok(place.bar[3] <= place.selection[1] && place.bar[1] >= place.top, `just above the selection, under the top bar: ${JSON.stringify(place)}`);
    assert.ok(place.bar[0] < place.selection[2] && place.bar[2] > place.selection[0], `over the selection: ${JSON.stringify(place)}`);
    await tap(".select-copy");
    await waitFor(`${copy}.dataset.state === "copied"`, "the copy");
    assert.deepEqual(await evaluate<string[]>(`[window.copied, ${copy}.textContent, getSelection().toString()]`), ["npm test", "Copied", "npm test"]);
    await waitFor(`${copy}.textContent === "Copy" && !${copy}.dataset.state`, "Copy again after the copy");
    assert.deepEqual(await bar(), { shown: true, wider: "Select message" }, "the bar stays with the selection");
    // Its other button selects the message, then the timeline, as Select all does; Copy copies what it selected.
    await tap(".select-wider");
    step = await selected();
    assert.equal(step.part, "div.markdown-content");
    assert.deepEqual(await bar(), { shown: true, wider: "Select all messages" });
    await tap(".select-copy");
    await waitFor(`${copy}.dataset.state === "copied"`, "the copy of the message");
    assert.equal((await evaluate<string>("window.copied")).trim(), step.text);
    await tap(".select-wider");
    assert.equal((await selected()).part, "ol#timeline.timeline");
    assert.deepEqual(await bar(), { shown: true, wider: null }, "only Copy for the timeline");
    // The selection going, or a tap elsewhere, takes the bar away.
    await evaluate("getSelection().collapseToStart()");
    await waitFor("document.querySelector('.select-bar').hidden", "the bar gone with the selection");
    assert.ok(await select(message, "commit"));
    await selectAll();
    assert.equal((await bar()).shown, true);
    await evaluate('document.getElementById("timeline").dispatchEvent(new PointerEvent("pointerdown", { bubbles: true }))');
    assert.equal((await bar()).shown, false, "a tap elsewhere");
    await evaluate('window.dispatchEvent(new PointerEvent("pointerup"))');
    // A copy the browser refuses says so, and the bar stays.
    await evaluate('Object.defineProperty(navigator, "clipboard", { configurable: true, value: { writeText: async () => { throw new Error("denied."); } } })');
    assert.ok(await select(message, "commit"));
    await selectAll();
    await tap(".select-copy");
    await waitFor('document.getElementById("toast").textContent === "Not copied: denied."', "the failure");
    assert.deepEqual(await bar(), { shown: true, wider: "Select all messages" });

    // ---- A thought of an open activity: the thought, the activity, the timeline ----
    await click("#timeline .act-toggle");
    await waitFor('document.querySelector("#timeline .act-body .msg-reasoning")', "the open activity");
    assert.ok(await select("#timeline .act-body .msg-reasoning", "build"));
    assert.deepEqual(await selectAll(), { part: "div.markdown-content", text: "Looking at the build first." });
    assert.equal((await bar()).wider, "Select work");
    assert.equal((await selectAll()).part, "div.act-body");
    assert.equal((await bar()).wider, "Select all messages");
    assert.equal((await selectAll()).part, "ol#timeline.timeline");

    // ---- Outside any part (the title of the session), the browser selects the page as before ----
    assert.ok(await select("#detail-title", "parser"));
    assert.ok(["html", "body"].includes((await selectAll()).part.split(/[#.]/u)[0]), "the whole page");
    await waitFor("document.querySelector('.select-bar').hidden", "no bar for the browser's own selection");

    // ---- A file shown full screen: its code, then its text, never the title or the session under it ----
    await evaluate('[...document.querySelectorAll("#timeline li.msg a")].find(a => a.textContent === "the notes").click()');
    await waitFor('document.querySelector(".output-viewer-drawn")?.textContent.includes("The rest of the notes.")', "the drawn file");
    assert.ok(await select(".output-viewer-drawn", "job"));
    assert.deepEqual(await selectAll(), { part: "code", text: "alta job start" });
    assert.equal((await bar()).wider, "Select file");
    step = await selectAll();
    assert.equal(step.part, "div.output-viewer-drawn.markdown-content");
    assert.deepEqual(await bar(), { shown: true, wider: null }, "only Copy for the whole file");
    assert.ok(step.text.startsWith("Notes") && step.text.endsWith("The rest of the notes."), step.text);
    assert.equal((await selectAll()).part, "div.output-viewer-drawn.markdown-content", "nothing wider in the file");
    assert.ok(await select(".output-viewer-title", "notes"));
    assert.equal((await selectAll()).part, "div.output-viewer-drawn.markdown-content", "from its title, the text of the file");
    assert.ok(await select(message, "commit"));
    assert.equal((await selectAll()).part, "div.output-viewer-drawn.markdown-content", "from the session under it, the text of the file");
    // As it is written, the same.
    await click(".output-viewer-switch");
    await waitFor('document.querySelector(".output-viewer-text")?.textContent.includes("alta job start")', "the file as it is written");
    assert.ok(await select(".output-viewer-text", "rest"));
    assert.equal((await selectAll()).part, "pre.output-viewer-text");
    assert.deepEqual(phone.failures, []);
  } finally {
    await phone.close();
  }
});
