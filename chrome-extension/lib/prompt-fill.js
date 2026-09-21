/** Placeholder fill + live trigger builders — ports MainWindow.xaml.cs. */

const ROUND_FILE = {
  "Recruiter Screen": "round_prompt_recruiter.md",
  "Hiring Manager": "round_prompt_hiring_manager.md",
  "Technical Screen": "round_prompt_technical_screen.md",
  "System Design": "round_prompt_system_design.md",
  "ML System Design": "round_prompt_system_design.md",
  "Behavioral": "round_prompt_behavioral.md",
  "Culture Fit": "round_prompt_behavioral.md",
  Peer: "round_prompt_behavioral.md",
  "Bar Raiser": "round_prompt_bar_raiser.md",
  "Skip Level": "round_prompt_exec_leadership.md",
  "Executive/VP": "round_prompt_exec_leadership.md",
  Final: "round_prompt_final_round.md",
  Presentation: "round_prompt_final_round.md",
  "Case Study": "round_prompt_hiring_manager.md",
  Research: "round_prompt_technical_screen.md",
  Code: "round_prompt_coding.md",
  Diagram: "round_prompt_system_design.md"
};

const ROUND_DISPLAY = {
  "Recruiter Screen": "Recruiter Screen",
  "Hiring Manager": "Hiring Manager",
  "Technical Screen": "Technical Screen",
  "System Design": "System Design",
  "ML System Design": "ML System Design",
  "Behavioral": "Behavioral",
  "Culture Fit": "Culture Fit",
  Peer: "Peer",
  "Bar Raiser": "Bar Raiser",
  "Skip Level": "Skip Level",
  "Executive/VP": "Executive / VP",
  Final: "Final Round",
  Presentation: "Presentation",
  "Case Study": "Case Study",
  Research: "Research",
  Code: "Coding",
  Diagram: "System Design"
};

function sub(text, pattern, value, flags = "") {
  if (!value || !String(value).trim()) return text;
  const v = String(value).trim();
  return text.replace(new RegExp(pattern, flags), () => v);
}

function fillIndividualPrompt(prompt, pi) {
  prompt = sub(
    prompt,
    "SENIORITY: Staff\\s+DOMAIN: AI/ML\\s+COMPANY STAGE: \\[fill\\]",
    `SENIORITY: ${pi.seniority}   DOMAIN: ${pi.domain}   COMPANY STAGE: ${pi.companyStage}`
  );
  prompt = sub(
    prompt,
    "COMPANY / PRODUCT / TEAM FOCUS / TECH STACK: \\[fill from research\\]",
    `Company: ${pi.company}\nProduct: ${pi.product}\nTeam Focus: ${pi.teamFocus}` +
      `\nKnown Tech Stack: ${pi.knownTechStack}\nRecent News: ${pi.recentNews}` +
      `\nInterviewer Background: ${pi.interviewerBackground}`
  );
  prompt = sub(
    prompt,
    "COMPANY CULTURE \\+ VALUES: \\[what they genuinely value\\. every culture/behavioral answer reflects this without reciting it\\]",
    `COMPANY CULTURE + VALUES: ${pi.companyValues}`
  );
  prompt = sub(
    prompt,
    "MY DIFFERENTIATION: \\[what makes me better than the typical candidate for this role\\]",
    `MY DIFFERENTIATION: ${pi.myDifferentiation}`
  );
  prompt = sub(
    prompt,
    "MY THROUGH-LINE: \\[.*?\\]",
    `MY THROUGH-LINE: ${pi.myThroughLine}`,
    "s"
  );
  prompt = sub(prompt, "RESUME: \\[paste\\]", `RESUME: ${pi.resumeSummary}`);
  prompt = sub(prompt, "JOB DESCRIPTION: \\[paste\\]", `JOB DESCRIPTION: ${pi.jobDescription}`);
  prompt = sub(
    prompt,
    "PROJECT STORIES \\(2 to 3, each with real numbers\\): \\[paste\\. setting and users, the business problem, what I built and my role, the decision I made and what I rejected, real before/after metrics\\]",
    `PROJECT STORIES (2 to 3, each with real numbers): ${pi.projectStories}`
  );
  prompt = sub(
    prompt,
    "SYSTEM DESIGN SCENARIOS \\(1 to 3, each with real numbers\\): \\[paste\\. what it does and its scale, the architecture I designed, key components and why, tradeoffs, how I handled scale and failure, real throughput/latency/cost\\]",
    `SYSTEM DESIGN SCENARIOS (1 to 3, each with real numbers): ${pi.systemDesignScenarios}`
  );
  return prompt;
}

function fillFullPrompt(prompt, pi) {
  prompt = sub(
    prompt,
    "- Seniority / Domain / Company / Company Stage: \\[what you read\\]",
    `- Seniority / Domain / Company / Company Stage: ${pi.seniority} / ${pi.domain} / ${pi.company} / ${pi.companyStage}`
  );
  prompt = sub(
    prompt,
    "- Key Skills \\(top 5\\) / Key Projects: \\[from resume\\]",
    `- Key Skills (top 5) / Key Projects: ${pi.keySkills} / ${pi.keyProjects}`
  );
  prompt = sub(
    prompt,
    "- JD Focus Areas \\(top 3\\): \\[from JD\\]",
    `- JD Focus Areas (top 3): ${pi.jdFocus}`
  );
  prompt = sub(
    prompt,
    "- My Differentiation: \\[what makes me unique vs a typical candidate at this level\\]",
    `- My Differentiation: ${pi.myDifferentiation}`
  );
  prompt = sub(
    prompt,
    "- My Through-Line: \\[the ONE impression every interviewer should describe in debrief\\. propose one from my materials\\]",
    `- My Through-Line: ${pi.myThroughLine}`
  );
  prompt = sub(
    prompt,
    "SENIORITY: Staff\\s+DOMAIN: AI/ML\\s+COMPANY STAGE: \\[Startup / Growth / Enterprise / Big Tech / AI-first\\]",
    `SENIORITY: ${pi.seniority}   DOMAIN: ${pi.domain}   COMPANY STAGE: ${pi.companyStage}`
  );
  prompt = sub(
    prompt,
    "Company / Product / Team Focus / Known Tech Stack / Company Values / Interviewer Background / Recent News: \\[fill\\]",
    `Company: ${pi.company}\nProduct: ${pi.product}\nTeam Focus: ${pi.teamFocus}\nKnown Tech Stack: ${pi.knownTechStack}\nCompany Values: ${pi.companyValues}\nInterviewer Background: ${pi.interviewerBackground}\nRecent News: ${pi.recentNews}`
  );
  prompt = sub(
    prompt,
    "COMPANY CULTURE \\+ VALUES: \\[what this company genuinely values, from research\\. every culture/behavioral answer reflects this without reciting it back\\]",
    `COMPANY CULTURE + VALUES: ${pi.companyValues}`
  );
  prompt = sub(
    prompt,
    "Typical Candidate Profile: \\[what a typical Staff AI/ML candidate looks like for this role\\]",
    `Typical Candidate Profile: ${pi.typicalCandidate}`
  );
  prompt = sub(
    prompt,
    "MY DIFFERENTIATION: \\[what makes me specifically better than that profile for THIS role\\]",
    `MY DIFFERENTIATION: ${pi.myDifferentiation}`
  );
  prompt = sub(
    prompt,
    "MY THROUGH-LINE: \\[the ONE impression every interviewer should independently describe in debrief,.*?\\]",
    `MY THROUGH-LINE: ${pi.myThroughLine}`,
    "s"
  );
  prompt = sub(prompt, "RESUME SUMMARY: \\[paste resume bullets\\]", `RESUME SUMMARY: ${pi.resumeSummary}`);
  prompt = sub(prompt, "JOB DESCRIPTION: \\[paste key JD requirements\\]", `JOB DESCRIPTION: ${pi.jobDescription}`);
  prompt = sub(
    prompt,
    "PROJECT STORIES \\(from the project-story companion\\. 2 to 3, each with real numbers\\): \\[paste\\..*?leadership answers\\]",
    `PROJECT STORIES (from the project-story companion. 2 to 3, each with real numbers): ${pi.projectStories}`,
    "s"
  );
  prompt = sub(
    prompt,
    "SYSTEM DESIGN SCENARIOS \\(from the system-design companion\\. 1 to 3, each with real numbers\\): \\[paste\\..*?\"how did you build\" answers\\]",
    `SYSTEM DESIGN SCENARIOS (from the system-design companion. 1 to 3, each with real numbers): ${pi.systemDesignScenarios}`,
    "s"
  );
  return prompt;
}

function fillOnboardingPrompt(prompt, ob) {
  prompt = sub(prompt, "\\*\\*Company / title / level / team:\\*\\* \\[fill\\]", `**Company / title / level / team:** ${ob.companyRoleTeam}`);
  prompt = sub(prompt, "\\*\\*Manager:\\*\\* \\[name, background, style\\]", `**Manager:** ${ob.manager}`);
  prompt = sub(prompt, "\\*\\*Today's week number:\\*\\* \\[Week n\\]", `**Today's week number:** Week ${ob.weekNumber}`);
  prompt = sub(prompt, "\\*\\*Their stack:\\*\\* \\[languages, cloud, ML infra, orchestration, data\\]", `**Their stack:** ${ob.stack}`);
  prompt = sub(prompt, "\\*\\*What they hired me to do:\\*\\* \\[one sentence\\]", `**What they hired me to do:** ${ob.hiredToDo}`);
  prompt = sub(prompt, "- Anchor project and metrics: \\[exact numbers, what each measured\\]", `- Anchor project and metrics: ${ob.anchorProject}`);
  prompt = sub(prompt, "- Scale facts: \\[team size, volume, model count\\]", `- Scale facts: ${ob.scaleFacts}`);
  prompt = sub(prompt, "- Gaps I openly named in the loop: \\[the honest nots\\]", `- Gaps I openly named in the loop: ${ob.gaps}`);
  prompt = sub(
    prompt,
    "- \\*\\*Why I left, exactly as I said it in the loop:\\*\\* \\[one forward looking sentence, never negative about the old place, never about a person or a client\\]",
    `- **Why I left, exactly as I said it in the loop:** ${ob.whyLeft}`
  );
  prompt = sub(
    prompt,
    "- \\*\\*My 40 second self intro, scripted:\\*\\* \\[.*?\\]",
    `- **My 40 second self intro, scripted:** ${ob.selfIntro}`,
    "s"
  );
  prompt = sub(
    prompt,
    "- How I scope before writing code / how I design and who I pull in",
    `- How I scope before writing code / how I design and who I pull in: ${ob.processScoping}`
  );
  prompt = sub(prompt, "- Build vs buy vs wait criteria", `- Build vs buy vs wait criteria: ${ob.processBuildBuy}`);
  prompt = sub(
    prompt,
    "- How I test and validate non deterministic model behavior",
    `- How I test and validate non deterministic model behavior: ${ob.processTesting}`
  );
  prompt = sub(
    prompt,
    "- How I ship: branching, review, staging, rollout, flags, rollback",
    `- How I ship: branching, review, staging, rollout, flags, rollback: ${ob.processShipping}`
  );
  prompt = sub(
    prompt,
    "- How I handle an incident / my model quality gates before a user sees it",
    `- How I handle an incident / my model quality gates before a user sees it: ${ob.processIncident}`
  );
  prompt = sub(
    prompt,
    "- How I report progress and risk upward, and how I disagree with someone senior",
    `- How I report progress and risk upward, and how I disagree with someone senior: ${ob.processReporting}`
  );
  prompt = sub(prompt, "\\*\\*People:\\*\\* \\[name, role, what they own, how they like to be approached\\]", `**People:** ${ob.carryoverPeople}`);
  prompt = sub(prompt, "\\*\\*Their acronyms and internal terms:\\*\\* \\[term, what it actually means\\]", `**Their acronyms and internal terms:** ${ob.carryoverAcronyms}`);
  prompt = sub(prompt, "\\*\\*Systems I now genuinely understand:\\*\\* \\[so I stop asking about them\\]", `**Systems I now genuinely understand:** ${ob.carryoverSystems}`);
  prompt = sub(prompt, "\\*\\*Open commitments I have made:\\*\\* \\[what, to whom, by when\\]", `**Open commitments I have made:** ${ob.carryoverCommitments}`);
  prompt = sub(prompt, "\\*\\*Work log entries:\\*\\* \\[from `GENERATE: carryover`\\]", `**Work log entries:** ${ob.carryoverWorkLog}`);
  return prompt;
}

function individualPromptUrl(round) {
  const file = ROUND_FILE[round] || "round_prompt_recruiter.md";
  return `prompts/Individual Prompt V2/${file}`;
}

function buildQuestionTrigger(segment, settings) {
  if (settings.activePanel === "onboarding") {
    return `THEY ASKED: "${segment}"`;
  }
  if (settings.promptModeIndividual) {
    return `QUESTION: "${segment}"`;
  }
  if (settings.selectedRound === "Diagram") {
    return `ROUND: System Design\nDIAGRAM: "${segment}"`;
  }
  const name = ROUND_DISPLAY[settings.selectedRound] || "Recruiter Screen";
  return `ROUND: ${name}\nQUESTION: "${segment}"`;
}

function buildPushbackTrigger(segment, settings) {
  if (settings.selectedRound === "Diagram") {
    return `DIAGRAM PUSHBACK: "${segment}"`;
  }
  return `pushed back with: "${segment}"`;
}

function buildTechTrigger(segment) {
  return `ROUND: Coding\nQUESTION: "${segment}"`;
}

if (typeof globalThis !== "undefined") {
  Object.assign(globalThis, {
    ROUND_FILE,
    ROUND_DISPLAY,
    fillIndividualPrompt,
    fillFullPrompt,
    fillOnboardingPrompt,
    individualPromptUrl,
    buildQuestionTrigger,
    buildPushbackTrigger,
    buildTechTrigger
  });
}
