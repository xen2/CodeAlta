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
  const { evaluate, waitFor, click } = phone;
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
  const selectAll = () => evaluate<{ part: string; text: string }>(`(() => {
    document.execCommand("selectAll");
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

    // ---- A thought of an open activity: the thought, the activity, the timeline ----
    await click("#timeline .act-toggle");
    await waitFor('document.querySelector("#timeline .act-body .msg-reasoning")', "the open activity");
    assert.ok(await select("#timeline .act-body .msg-reasoning", "build"));
    assert.deepEqual(await selectAll(), { part: "div.markdown-content", text: "Looking at the build first." });
    assert.equal((await selectAll()).part, "div.act-body");
    assert.equal((await selectAll()).part, "ol#timeline.timeline");

    // ---- Outside any part (the title of the session), the browser selects the page as before ----
    assert.ok(await select("#detail-title", "parser"));
    assert.ok(["html", "body"].includes((await selectAll()).part.split(/[#.]/u)[0]), "the whole page");

    // ---- A file shown full screen: its code, then its text, never the title or the session under it ----
    await evaluate('[...document.querySelectorAll("#timeline li.msg a")].find(a => a.textContent === "the notes").click()');
    await waitFor('document.querySelector(".output-viewer-drawn")?.textContent.includes("The rest of the notes.")', "the drawn file");
    assert.ok(await select(".output-viewer-drawn", "job"));
    assert.deepEqual(await selectAll(), { part: "code", text: "alta job start" });
    step = await selectAll();
    assert.equal(step.part, "div.output-viewer-drawn.markdown-content");
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
