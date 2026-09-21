# COMPANIONS INDEX

Five generators, one per input block of the onboarding assistant. Each is a standalone prompt. Paste one into a fresh session, feed it your sources, take the block it produces, drop it into `onboarding_assistant_prompt_v3.md`.

None of these are live speaking prompts. They are analysis tools and they behave like normal assistants.

## Which one fills what

| Companion | Fills | Run when |
|---|---|---|
| 1. Company and role | Top of MY MATERIALS | Once, before week one |
| 2. Interview loop lock | LOCKED FROM MY INTERVIEW LOOP, plus why I left and the self intro | Once, before week one |
| 3. Working process elicitor | MY WORKING PROCESS | Once, before week one. Budget thirty minutes |
| 4. Work log entry | MY WORK LOG | End of each week, or when I finish something |
| 5. Carryover builder | MY CARRYOVER | End of each day in week one, then weekly |

## Setup order

Run 1, 2 and 3 before the first day. Together they produce a complete MY MATERIALS. Companion 3 is the one that takes real effort and it is the one that matters most, because MY WORKING PROCESS is what makes me sound like one consistent engineer to ten different people who compare notes.

Companions 4 and 5 are the running loop. They are what keep the assistant current instead of frozen on day one.

## The weekly loop

Friday, or whenever a week closes:

1. Paste the week's tickets, PRs and notes into Companion 4. Answer its three questions per item. Keep the entries and the FACTS LOCKED list.
2. Paste last week's carryover plus this week's meeting notes into Companion 5. Keep the new carryover and the blind spots list.
3. Update the week number in MY MATERIALS. The assistant sets my posture from it.
4. Read the blind spots list. Those are the questions for next week.

## Two outputs worth reading, not just pasting

**Companion 2's `UNCERTAIN, DO NOT SAY` list.** Claims where I cannot confirm what I said in the loop, with the conservative version to use. Read this before week one and again before any meeting with someone who interviewed me.

**Companion 5's `THIS WEEK'S BLIND SPOTS`.** Things that keep coming up that I still do not understand, with who to ask. This is the single most useful artifact the system produces, because the quality of my questions is what builds credibility in the first month, not the polish of my answers.

## Why the three-question pattern appears in 4 and 5

A ticket records what was requested. It never records what actually happened. What went wrong on the way, the real decision, and the open thread live only in my head, and they are exactly the fields the assistant's familiarity rules draw from. Without them a spoken update sounds like someone reading a ticket aloud. Answer those three honestly and briefly and everything downstream works.

## If the assistant starts saying things I do not recognize

That is fabrication drift, and it means the work log is thin rather than that the prompt is broken. Fire `LOG ONLY` mid meeting, then rerun Companion 4 with better sources before the next session.
