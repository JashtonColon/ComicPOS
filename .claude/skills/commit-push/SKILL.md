---
name: commit-push
description: Commit the uncommitted changes in logical groups with clear messages, push the branch, then merge into main only after the user confirms.
disable-model-invocation: true
argument-hint: "[optional branch name]"
---

# /commit-push

Run this end to end. Explain each grouping and message choice briefly (this
project's rule: teach, don't just do).

## 1. Look before touching anything
- `git status --porcelain -uall`, `git diff --stat`, and read the diffs.
- Note the current branch.

## 2. Leave out what doesn't belong in git
- Never commit: `*.bak*` files, one-off `_fix_*.py` / patch scripts,
  `graphify-out/`, `checknow-debug.txt`, `.env` or anything with secrets,
  `bin/`, `obj/`.
- If a new kind of junk file appears, propose a `.gitignore` entry and
  commit that first as `chore: ignore ...`. Never delete files.

## 3. Branch
- If on `main`: create a branch. Use `$ARGUMENTS` as the name if given,
  otherwise `fixes/<YYYY-MM-DD>-<short-topic>` (topic from the changes).
- If already on a feature branch: keep using it.

## 4. Commit in logical groups
- One commit per fix or feature, not one giant commit. A fix spanning
  interface + implementation + page goes together, unless the user asked
  for one commit per file.
- Order commits so each one builds (a method before its callers).
- Message format: `type(area): summary` (types: feat, fix, chore, docs,
  refactor, style), then a short body explaining WHY, not just what.
- End every message with the attribution trailer from the current session's
  instructions, if one is given.

## 5. Verify
- `dotnet build`. If the running app locks files, build the changed project
  alone or with `-o "$TEMP/cp-verify"`. If it really fails, stop and
  report; don't push broken code.

## 6. Push
- `git push -u origin <branch>`.

## 7. Merge - ASK FIRST
- Show the commit list (`git log --oneline main..<branch>`) and ask:
  "Merge these into main and push main?" Wait for a clear yes.
- On yes: `git switch main`, `git pull --ff-only`,
  `git merge --no-ff <branch>`, `dotnet build`, `git push`.
- If the merge conflicts: stop, show the conflicting files, and ask how to
  resolve. Never pick a side silently.
- After a successful merge, offer (don't do) deleting the branch locally
  and on origin.

## Never
- Force-push, `--no-verify`, rewrite pushed history, or merge without the
  user's yes in this run.
