# Feature Value: Prompt Columns & Ask AI

*Usability and strategic importance of two of DocIntel's core workspace features.*

This document describes what Prompt Columns and Ask AI do, how a user actually
experiences them, and why each matters to the product. Descriptions are grounded
in the current implementation, not aspiration.

---

## 1. Prompt Columns

### What it is
A **Prompt Column** is an admin-defined custom column that runs a natural-language
instruction against every processed document (or a filtered subset) and stores the
model's answer as a cell in the document table. It turns an open-ended question —
"summarize this," "verify the contract identifiers," "flag anything unusual about
pricing" — into a **structured, repeatable column** that appears for every document,
old and new.

Each column (`PromptColumnDefinition`) carries:
- a **name** (the column header),
- **prompt instructions** that support templating such as `{{Document.Content}}`,
- an optional **entity filter** (`FilterMode` of `All` / `And` / `Or` over rules like
  `Contract Number Exists`, `Billing Currency Equals USD`, `Customer Name Contains …`),
  using operators `Exists`, `Equals`, `Contains`, `StartsWith`, `NotEquals`,
- an **active** toggle and an optional per-column **model override**.

### How it works (brief)
During document processing, the prompt-columns step runs per document slice:
1. It first evaluates the column's **entity filter** against the slice's already-extracted
   entities. Documents that don't match are skipped (the column is simply blank for them),
   so the expensive model call only happens where it's relevant.
2. For matching documents it sends the **actual PDF** (as a base64 `application/pdf`
   payload, where the model supports PDF input) plus the prompt, and stores the answer
   as a `PromptColumnResult` (`ResultText` + `FilterMatched`).
3. Results are **searchable** — document search matches on prompt-column result text —
   and columns can be **re-evaluated on an existing document** via
   `POST /jobs/{jobId}/evaluate-prompts` without reprocessing the whole document.

The default workspace ships with example columns (*Summary*, *Highlights*,
*Contract Check*) so the capability is legible on day one.

### Usability
- **Zero-code configuration.** A business admin writes an instruction in plain language
  in the Admin UI; there is no pipeline to build. Saved columns stay visible while the
  create/edit form is disclosed on demand.
- **Spreadsheet-shaped output.** Answers land as columns in the familiar document table,
  so the mental model is "a smart spreadsheet of my documents," not "an AI tool." Results
  are scannable, sortable context alongside the document rather than a separate chat.
- **Targeted by design.** The entity filter means a column like *Contract Check* only runs
  where a contract/MBSA number exists — users aren't paying for or reading irrelevant cells.
- **Retroactive.** Re-evaluation lets an admin add or refine a column and backfill it onto
  documents already in the workspace, so the table stays consistent as questions evolve.

### Importance
- **It productizes repeatable judgment.** Any question a reviewer asks of *every* document
  becomes a one-time setup instead of per-document manual work. That is the difference
  between "AI that answers when asked" and "AI that has already answered."
- **It's the structured counterpart to Ask AI.** Where Ask AI is exploratory and
  conversational, Prompt Columns are deterministic, always-on, and comparable across a
  whole corpus — the right tool when you need the *same* answer for *every* document.
- **Low-friction expansion surface.** New customer use cases usually map to "add a column,"
  which keeps onboarding cheap and makes the product feel configurable to each team without
  engineering involvement.

---

## 2. Ask AI

### What it is
**Ask AI** is DocIntel's agentic analytical engine: a user asks a question in natural
language and the agent reasons over the workspace's processed documents, inspects the
evidence, and returns a **grounded, cited answer** — not a free-floating LLM response.
It is the interactive, exploratory way into the same document intelligence that Prompt
Columns expose as fixed columns.

### How it works (brief)
- The agent runs a tool-using loop over the document corpus (search, inspect fields and
  evidence, run analyses), bounded by a budget, and assembles an answer with **numbered
  citations** that map to specific documents and **page ranges** (`run.Citations`,
  `EvidenceIds`).
- Answers are **grounded**: the engine enforces that a document answer must cite inspected
  evidence, and conversational answers may not fabricate citations. A prompt-boundary policy
  treats document content as data, not instructions (prompt-injection defense).
- Runs are **durable and asynchronous**: a question becomes an `AskAiRun`, is queued, and is
  processed by a worker; clients poll for the result. The same run can be delivered over a
  channel (email, Teams).
- Before an answer is finalized or delivered, the engine **re-resolves the asker's
  permissions and re-checks that every referenced document is still available and completed**,
  and aborts if access changed in the meantime. Answers never leak documents the asker can
  no longer read.

### Usability
- **Ask in plain language, trust the answer.** Because every claim carries a citation to a
  document and page, users can verify rather than take the model on faith — essential for
  legal/commercial review where a wrong answer is expensive.
- **Meets users where they work.** The same engine is reachable from the web workspace and
  from Teams and email, so a user can @mention the bot or email a question and get a cited
  answer with a sign-in-protected link to the full result.
- **Safe by construction.** The permission recheck and prompt-boundary policy mean the
  feature behaves predictably under real-world conditions (revoked access, deleted documents,
  adversarial document text), which is what makes it usable in regulated settings.

### Importance
- **It is the product's reasoning layer.** Classification, extraction, splitting and Prompt
  Columns produce structured facts; Ask AI is where a human asks open questions *across* those
  facts and documents. It's the feature that feels like a knowledgeable colleague.
- **Grounding + citations are the moat.** Generic chatbots answer confidently and wrongly.
  DocIntel's value is that an answer is traceable to source pages and scoped to what the user
  may see — a defensible compliance story, not just a UX nicety.
- **It is the composable engine for everything proactive.** The durable run + channel delivery
  + permission-recheck design is exactly what a proactive/standing-instructions capability
  reuses. Investment in Ask AI's correctness and cost compounds across future features.

---

## 3. How they complement each other

| | Prompt Columns | Ask AI |
|---|---|---|
| **Interaction** | Configured once, runs automatically | Asked interactively, per question |
| **Output** | Structured column on every document | Cited narrative answer |
| **Scope** | Same question across the whole corpus | Any question, often across documents |
| **Best for** | Repeatable review checks, triage, comparison | Exploration, synthesis, one-off questions |
| **Shared foundation** | Model routing, extracted entities, the processed document | Model routing, grounding/citations, permissions |

They are two front doors to the same document intelligence. Prompt Columns answer
*"what do I need to know about every document?"* in advance; Ask AI answers
*"what do I need to know right now?"* on demand. A healthy workspace uses columns to make
the corpus scannable and Ask AI to dig in where a column surfaces something worth a closer look.

---

## 4. Considerations & limitations

- **Cost and latency scale with use.** Prompt Columns run a model call per matching document;
  broad columns with loose filters are the main cost lever — encourage tight entity filters.
  Ask AI runs an agentic loop per question; large multi-document questions are the latency risk.
- **Quality depends on upstream extraction.** Entity filters are only as good as the extracted
  entities; a mis-extracted field means a column silently doesn't run for that document.
- **Answers are grounded but not infallible.** Citations make verification possible; they do not
  remove the need for a human to verify high-stakes conclusions. The product is explicitly
  "machine-extracted," not a human-verification guarantee.
- **Both inherit the workspace permission model.** A user only ever sees column results and Ask AI
  answers for documents they are allowed to read; this is enforced, but it also means results can
  legitimately differ between users in the same workspace.

---

## 5. Bottom line

Prompt Columns turn repeatable review judgment into zero-code, always-on structured output;
Ask AI turns the same underlying document intelligence into an interactive, cited, permission-safe
analyst. Prompt Columns make a corpus **scannable**; Ask AI makes it **answerable**. Together they
are the clearest expression of DocIntel's thesis — document intelligence you can both automate and
interrogate, with every answer traceable to the source.
