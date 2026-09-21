# DRIFT REMINDER CARD

Keep this open in a second window. Paste one line, nothing else. The model re-answers the last question corrected. It should never acknowledge the nudge.

## The single most common drift, by turn

**Turns 1 to 5.** Rarely drifts. If it does, it is mode confusion. Fix with `ABSORB` or `REPORT`.
**Turns 6 to 12.** The danger zone, same as the interview prompt. Length creep, then structure collapse, then character break. Fix with `SHORTER`, then `NO STRUCTURE`, then `RESET`.
**Turns 13 plus.** Fabrication drift. The model starts filling in plausible file names, numbers and library versions that are not in my log. This is the dangerous one. Fix with `LOG ONLY`.

## The nudges

`RESET` reload all six rules and the mode, re-answer clean
`SHORTER` half the length, same content
`NO STRUCTURE` spoken sentences only, kill every list and section
`ABSORB` force absorb mode, short and curious
`REPORT` force report mode, three beats
`LOG ONLY` strip every fact not in my work log or carryover
`NO NUMBERS` strip metrics and estimates
`SOFTER` strip the criticism, convert to a history question
`MORE HUMAN` kill ticket language, add the friction detail, end on the open thread
`FORMAT` restore one sentence per line, bold stress words, slashes at pauses
`LESS HEDGE` too deferential, give me the bounded view

## The full nudge, when several slipped at once

Absorb mode: **Under 30 words, no lists, no metrics, ask something.**

Report mode: **Under 60 words, no lists, one artifact, end on what's open.**

## Diagnosing by what I see on screen

**It wrote a paragraph block.** `FORMAT`
**It wrote three sections with headers.** `NO STRUCTURE`
**It gave me a metric in a walkthrough.** `ABSORB`
**It said a filename I do not recognize.** `LOG ONLY`, immediately, before I speak
**It said "that should be pretty quick."** `NO NUMBERS`
**It sounds like a Jira comment.** `MORE HUMAN`
**It ended with a tidy summary sentence.** `MORE HUMAN`
**It hedged three times in one answer.** `LESS HEDGE`
**It offered me a shorter version.** `RESET`, character has broken
**Two answers in a row opened with "Yeah."** `FORMAT` then keep going

## If it drifts three times in the same session

Do not keep nudging. Reload the whole prompt in a fresh session with MY CARRYOVER pasted in. Fresh context resets adherence far better than repeated correction, and this is a long meeting problem, not a prompt problem.

## Between meetings

Run `GENERATE: carryover` before closing the session. Paste the result into MY CARRYOVER for the next one. That block is the only thing carrying my accumulated context forward.
