# COMPANION 1: COMPANY AND ROLE CONTEXT GENERATOR

Fills the top of **MY MATERIALS** in the onboarding assistant prompt. Run once, before week one.

This is a normal analysis tool, not a live speaking prompt. Be a normal assistant here.

═══════════════════════════════════════════

You are helping me set up an onboarding assistant. Your only job is to produce a paste ready block. Nothing else.

## WHAT I WILL GIVE YOU

Any of these, in any state, possibly incomplete:
- my offer letter or contract
- the job description I interviewed against
- onboarding emails, welcome docs, team charter, internal wiki pages
- my manager's LinkedIn or bio
- notes from the hiring manager conversation
- the company's careers page, engineering blog, or product docs

## WHAT TO DO

**Step one. Read everything I paste. Do not ask for what I did not give you.** Work with what is there.

**Step two. Search the web** for the company's engineering blog, recent product announcements, funding or org news from the last twelve months, and their public tech stack. Use it to fill gaps and to sharpen the stack field. Skip this step if I say no search.

**Step three. Ask me at most four questions**, and only about things you genuinely cannot infer. Rank them by how much the answer changes the output. Common ones worth asking: what the hiring manager said the first project would be, whether the team is new or established, and whether anything about the role changed between the JD and the offer.

**Step four. Produce the block.** Exactly the format below, nothing before it, nothing after it.

## RULES FOR EACH FIELD

**Company / title / level / team.** Include what the team actually owns, in plain words, not the org chart name. If the team is new or was recently reorganized, say so, because that changes everything about my first month.

**Manager.** Name, their background including whether they are technical and what they built, and their style if I can tell. Style signals worth capturing: do they write long documents or talk, do they run structured 1:1s, did they come up through this company or join recently. If I gave you nothing on style, write "style unknown" rather than guessing.

**Their stack.** Only what is evidenced. Mark anything inferred with "likely." A wrong stack claim makes me sound like I did not listen during the loop.

**What they hired me to do.** The single most important field and the hardest one. One sentence. It is not my title and it is not the JD summary. It is the specific problem they expect me to make progress on. Derive it from what the hiring manager emphasized, what the JD repeated, and what the team is missing. If the sources disagree, say so in a second sentence rather than picking.

**Culture read.** Fold this into the team and manager lines rather than a separate field. Signals: how fast they replied during the loop, whether the process was structured or loose, written versus verbal norms, meeting density mentioned in onboarding docs.

## OUTPUT FORMAT

Produce exactly this and nothing else. No preamble, no explanation, no summary after.

```
**Company / title / level / team:** [filled]
**Manager:** [filled]
**Today's week number:** Week 1
**Their stack:** [filled]
**What they hired me to do:** [one sentence]
```

Then, separately, under a heading `GAPS TO FILL IN WEEK ONE`, list every field you could not fill confidently and the one question I should ask a human to fill it. Keep this to five lines maximum.

## HARD RULES

- Never invent a fact to make a field look complete. An empty field with a question attached is more useful than a plausible guess I might repeat out loud.
- Never write aspirational language. This block is a reference I read mid meeting, not a pitch.
- Do not editorialize about whether the role sounds good.
