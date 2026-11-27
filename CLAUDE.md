# ComicPos — ground rules for Claude Code sessions

This file is read automatically by Claude Code at the start of every session
in this folder. It exists so a fresh CLI session inherits the same ground
rules this project has been built under via Claude.ai (Cowork), without the
user having to re-explain them.

## The one rule that matters most

**Teach, don't just do.** The user's standing instruction for this whole
project: *"i want you to teach me how to make this system of a pos dont
just do it for me."* Explain the root cause of a bug before fixing it,
explain *why* a design choice was made, and prefer giving the user enough
understanding to make the next call themselves over silently doing
everything end-to-end. This applies even when working autonomously/in the
background — leave a clear trail (comments, commit messages, chat
explanation) of the *why*, not just the diff.

## What this project is

A POS (point-of-sale) system for a comic book store, in C#/.NET:
- `ComicPos.Web` — Blazor Server app, the actively-developed UI (staff-facing
  checkout, inventory, pull lists, new-books/solicitation intake, FOC
  reporting, settings).
- `ComicPos.App` — an older WinForms UI. **Gets zero new feature work** (see
  below) — it's kept building/running but not actively extended.
- `ComicPos.Core` / `ComicPos.Data` / `ComicPos.Importers` — shared domain
  models, EF Core + SQLite data access, and per-source catalog
  parsers/importers (Lunar Distribution, PRH/Penguin Random House, Metron,
  LOCG, Comic Vine, ComicList/Comicade, etc).
- Data lives in SQLite (`comic_pos.db`) in a sibling `ComicPos-Data` folder,
  not inside this repo.

The system ingests weekly/monthly distributor catalogs (Lunar's own weekly
`.xlsx`, PRH's "Comics Retail" catalog + per-publisher pages, Metron's API,
etc.), matches them against existing Series/Issues, backfills cover art and
variant covers, manages customer pull-list subscriptions, and is meant to
eventually email an admin a compact "what to buy before FOC" view of
subscriber pull lists.

## Standing constraints (do not relax these without the user explicitly asking)

- **No raw SQL mutations against the live database, ever.** Every data
  change goes through the app's own C# service methods (`AutoFetchService`,
  `WorkerService`, etc). Read-only `sqlite3 ... "SELECT ..."` for
  inspection/diagnosis is fine; direct `UPDATE`/`DELETE`/`INSERT` against
  `comic_pos.db` is not.
- **`ComicPos.App` (WinForms) gets zero new feature work.** Bug fixes that
  live in a *shared* service (`ComicPos.Core`/`ComicPos.Data`) are fine and
  both UIs benefit automatically — but don't add WinForms-specific UI work.
- **Git: only commit when the user explicitly asks.** Don't auto-commit as
  part of "finishing" a task. (This repo has a remote at `origin/main`.)
- **Never skip git hooks, never force-push, never bypass signing**, unless
  explicitly asked.
- **Destructive file deletion needs a real decision, not a reflex.** Prefer
  moving stray files aside or just flagging them over deleting outright,
  unless the user has clearly said to delete.
- **Every substantive code edit should be verified structurally before
  calling it done**: backup the file first (or confirm git can diff it),
  check brace/paren balance, and re-read the touched region. This project's
  history (see below) was built for a long stretch with no working
  `dotnet build` available to the assistant — if that's still true for you,
  say so plainly rather than claiming something is "tested" when it isn't.
  **If you (the Claude Code session reading this) DO have real `dotnet`
  access via your own shell, that's a genuine upgrade over the old
  constraint — use it, and say so, rather than silently keeping the old
  habit out of inertia.** The user does their own `dotnet build`/run testing
  either way; don't claim a fix is build-verified unless you (or they) have
  actually run the compiler.

## Full project history lives in the claude.ai Project "comic book pos"

This repo's own commit history is sparse relative to how much work has
actually happened — most of this project to date was built through a
claude.ai Cowork session, not Claude Code, and that session kept a detailed
running log as **docs inside the claude.ai Project "comic book pos"**
(visible in claude.ai / the Claude apps, not in this git repo). A Claude
Code CLI session has no automatic access to that Project — if you need deep
history on a specific bug or decision, ask the user to paste the relevant
section, or ask them to export it.

As of 2026-11-27 that Project contains (newest first):
- `claude/full-reset-2026-11-27.md` — a full DB reset that was performed,
  plus the Lunar Distribution "fake 1/1/0001 sentinel" bug (root cause +
  fix) and the Settings/Worker lockout chicken-and-egg bug (root cause +
  fix: seeds a default Admin/9876 worker on Web startup when Workers table
  is empty).
- `claude/db-cleanup-and-task-audit-2026-11-26.md` — a DB/disk cleanup pass,
  stray-file inventory, the New Books "±3 months" date-filter bug and a
  Lunar FOC-date-parsing (year-1900) bug.
- `claude/prh-upc-hyperlink-pivot.md` — how the PRH (Penguin Random House)
  cover-variant pipeline evolved: from a planned LOCG-based approach, to
  using PRH's own UPC-hyperlink/book-page data instead, including several
  rounds of real-world trace debugging.
- `claude/bug-22-hc-tp-not-duplicates.md` — the saga of the duplicate-series
  detector incorrectly merging (or incorrectly *not* merging) hardcover vs.
  trade-paperback vs. plain editions of the same book.
- `claude/metron-integration-notes.md` — the single largest doc: the
  Metron.cloud integration, then Lunar Distribution's weekly-release-file
  automation, then PRH's full catalog-automation reverse-engineering, a long
  chain of barcode-collision/cover-variant bugs (numbered Bug #1 through
  Bug #23+), and the whole "which comic-data source works/doesn't" story
  (Metron IP-blocked, LOCG Cloudflare-blocked, Comic Vine ToS risk flagged).
- `old talk with gemeni` — the original brainstorming conversation (with
  Gemini, not Claude) that chose the C#/.NET + SQLite + WinForms-then-Blazor
  stack in the first place, and surveyed where weekly/monthly comic
  solicitation data can actually be sourced from (Lunar, PRH, Diamond,
  ComicList/Chuck's Comic List, LOCG).

If the user wants this history physically duplicated into this repo (e.g.
under a `docs/` folder) rather than just referenced here, ask them — it's a
few hundred KB of markdown and is easy to do on request, just wasn't copied
by default to avoid bloating the repo with content that already lives
somewhere durable.

## Known-open items as of 2026-11-27 (ask the user before assuming these are stale)

- Task #75 — 4 weeks of Lunar backfill history that failed to import, not
  fully recovered.
- Task #115 — parallelizing `ClearPlaceholderCovers()` (explicitly deferred
  by the user).
- A pile of `.bak*`/`_fix_*.py` files at the repo root and inside
  `ComicPos.Data/` from this editing pattern — harmless, untracked, safe to
  leave or clean up, not safe to assume are "finished work in progress."
- The Lunar Distribution archive-walk fix and the default-admin-worker fix
  (both described in `full-reset-2026-11-27.md`) were not yet build-tested
  as of this file's writing.
