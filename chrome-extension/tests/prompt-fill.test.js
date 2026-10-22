const test = require("node:test");
const assert = require("node:assert/strict");
const {
  sanitizeSegment,
  buildQuestionTrigger,
  buildPushbackTrigger,
  buildTechTrigger,
  individualPromptUrl
} = require("../lib/prompt-fill.js");

const full = { selectedRound: "Hiring Manager" };

test("question trigger carries the round and the quoted segment", () => {
  assert.equal(
    buildQuestionTrigger("why this company", full),
    'ROUND: Hiring Manager\nQUESTION: "why this company"'
  );
});

test("individual mode and onboarding drop the round", () => {
  assert.equal(buildQuestionTrigger("x", { promptModeIndividual: true }), 'QUESTION: "x"');
  assert.equal(buildQuestionTrigger("x", { activePanel: "onboarding" }), 'THEY ASKED: "x"');
});

test("diagram round asks for a diagram", () => {
  const diagram = { selectedRound: "Diagram" };
  assert.equal(buildQuestionTrigger("a cache", diagram), 'ROUND: System Design\nDIAGRAM: "a cache"');
  assert.equal(buildPushbackTrigger("add a queue", diagram), 'DIAGRAM PUSHBACK: "add a queue"');
});

test("pushback and coding triggers", () => {
  assert.equal(buildPushbackTrigger("is it scalable", full), 'pushed back with: "is it scalable"');
  assert.equal(buildTechTrigger("reverse a list"), 'ROUND: Coding\nQUESTION: "reverse a list"');
});

test("quotes in the segment cannot close the quoted question", () => {
  assert.equal(
    buildQuestionTrigger('they said "tell me" about “scale”', full),
    "ROUND: Hiring Manager\nQUESTION: \"they said 'tell me' about 'scale'\""
  );
});

test("line breaks and repeated spaces collapse", () => {
  assert.equal(sanitizeSegment("  one\r\ntwo\n\n  three \t"), "one two three");
  assert.equal(sanitizeSegment("   "), "");
  assert.equal(sanitizeSegment(null), "");
});

test("a long segment keeps its most recent words", () => {
  const long = "oldword ".repeat(2000) + "the actual last question";
  const out = sanitizeSegment(long);
  assert.ok(out.length <= 4002);
  assert.ok(out.startsWith("… "));
  assert.ok(out.endsWith("the actual last question"));
});

test("round names map to prompt files", () => {
  assert.equal(
    individualPromptUrl("Code"),
    "prompts/Individual Prompt V2/round_prompt_coding.md"
  );
  assert.equal(
    individualPromptUrl("nonsense"),
    "prompts/Individual Prompt V2/round_prompt_recruiter.md"
  );
});
