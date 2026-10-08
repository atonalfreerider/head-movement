# Git policy for head-movement (the Unity viewer)

This repository is **public** (MIT licence). Everything committed here is readable by anyone, for good,
including every older commit. Treat each commit as a publication.

The pipeline code, the dancer-specific data and the analysis outputs live in the separate **workspace
repository** (`dancecap/docs/GIT.md` there) and in git-ignored folders. This file covers only the viewer.

## Branches

| Branch | Role |
|---|---|
| `main` | The integration branch. All finished work lands here and the working tree should sit on it. |
| `atlas-prototype` | Pointed at the same commit as `main` when this policy was written. Treat it as a legacy alias: do not commit to it. |
| `adv-hair`, `cloth`, `floorcraft`, `open-xr`, `paper-doll` | Old experiment branches on the remote. Leave them alone (no deletes, no rebases). |
| `topic/<short-name>` | Optional local branch for a risky or long change. Merge it with `git merge --ff-only` (or `--no-ff` when the group should stay visible), then delete the local branch. |

Rules:

- Small, finished commits go straight to `main`. A larger or risky piece gets a topic branch first.
- **No force-push, no history rewrite** of anything that was ever pushed. Cleaning up old history would need a
  rewrite and a force-push; that is the repository owner's decision, never an agent's.
- Checkpoints are marked with annotated local tags named `checkpoint-YYYY-MM-DD` (add `-<topic>` when there are several in a day). Tags stay local until the owner pushes.
- Agents never create, delete or move remote branches or tags, and never change the repository settings.

## Commit conventions

- Subject: imperative, aim for 72 characters and never exceed 110, optional area prefix (`avatar:`, `hair:`, `overlays:`, `floor:`,
  `dance graph:`, `editor CLI:`, `tools:`, `docs:`, `build:`, `chore:`). Example: `overlays: draw the follower spine as a bead chain`.
  Some subjects of the 2026-10-08 series are longer than 72; they stay as they are.
- Body: what changed and why. When the user asked for the behaviour, quote the request in quotation marks
  (short, with the date). Mention anything left out on purpose.
- One logical change per commit. A Unity `.meta` file is committed together with its asset, never alone.
- Agent commits end with the trailer line `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`.
- Intermediate commits of a multi-file feature are **checkpoints**: say so in the subject
  (`checkpoint: ...`). The tree is only guaranteed to compile at the end of a group, so before the last commit of a
  group check the Unity console (`recompile_status` / `console_status`: `compilationFailed` must be false).
- Commit messages are scanned by the same rules as the files (see Hooks), so keep them free of private data.

## What must never be committed, and why

| Never | Why |
|---|---|
| Likeness or appearance data of real people: portraits, textures, hair grooms, face or body shape values, colours sampled from a person | Privacy and consent. This repository is public. |
| The names of the people in the recordings, the song title and its artists, third-party character or franchise names, the user's private move-graph file name | Privacy and copyright. Use neutral wording: "the leader", "the follower", "a demo take", "the user's move-graph file". |
| Audio and video: songs, narration, recordings, footage (`Recordings/`, `*.mp4`, `*.wav`, ...) | Copyright and personal data. Anything under `Recordings/` is refused by the hook even with `git add -f`. |
| Capture data: everything under `Assets/StreamingAssets/` except `.gitkeep`, point clouds, splats, `.npz`, `.pkl`, checkpoints | Personal data, size. It is git-ignored on purpose, and the hook refuses it even with `git add -f` (rule `private-path`). |
| Gated body models (SMPL-X, FLAME, MANO) and their derived data | Their licences forbid redistribution. This repository holds loaders only. |
| Keys, tokens, passwords, `.env`, `*-key.txt`, certificates | Security. Code may name a key **file path** to read at run time, never a key value. |
| Absolute paths containing a user directory, machine-specific settings | Privacy and portability. Use relative paths; per-machine defaults go to the git-ignored `Tools/local.ps1`. |
| Files above 5 MB (except the allow list) and built binaries | Repository size. |

If you need such data to test something, keep it in a git-ignored folder, in the workspace repository, or in
`Tools/local.ps1`. If you are unsure whether something may be published, do not commit it and ask the owner.

## Hooks (Tools/githooks)

Two local hooks run `Tools/githooks/guard.py` (Python standard library only, identical copy in the workspace repository):

- `pre-commit` checks what is staged. `commit-msg` checks the message. `pre-push` refuses deletions and non-fast-forward updates
  and audits every commit that would be sent (messages and added lines, with the same rules), which also catches commits made
  with `--no-verify` and checks a push of old local commits before anything leaves the machine.
- Install once per clone (it only sets `core.hooksPath`; nothing is shared or pushed):
  `powershell -File Tools/install_hooks.ps1` or `sh Tools/install_hooks.sh` (`--check` shows the state, `--no-test` skips the self-test).
- The hooks need Python 3.8+. They look for `python3`, `python`, then the workspace virtual environment; set `HM_GUARD_PYTHON` to force one.

Rules (the id is printed in brackets when a commit is blocked):

| Id | Blocks |
|---|---|
| `key-file` | key, token, `.env`, credential and certificate file names; the private terms file |
| `secret` | secret patterns in added lines (API keys, bearer tokens, private-key blocks, URL credentials, long secret-looking assignments) and the exact value of any local `*-key.txt` file |
| `gated-model` / `model-data` | body-model files, weights, arrays, pickles, point clouds, splats |
| `media` | audio and video files |
| `binary-asset` | a **new** image or 3D-model file (it might carry a likeness) unless it matches the allow list |
| `likeness` | file names that look like portraits, textures or grooms of a person (not applied to source and docs) |
| `private-path` | anything under `Assets/StreamingAssets/` except `.gitkeep` and `.gitkeep.meta`, and anything under `Recordings/`; never allow-listable, and not bypassed by `git add -f` |
| `size` | files above 5 MB unless allow-listed |
| `term` | a line or a new path that contains an entry of the private terms list |
| `local-path` | an absolute path with a user directory |
| `push` | (`pre-push`) a deletion of a remote ref or a non-fast-forward update |

Details:

- **Private terms list.** One entry per line in `~/.hm-forbidden-terms.txt`, **outside every repository**. It is never
  committed and never quoted in any file, commit message or report. The hook matches case-insensitively, ignores accents, and
  tolerates separators between words (`a b`, `a_b`, `a-b`, `AB`). It only reports `term #N`, where N is the line number of the
  entry in that file, and it masks the term in printed paths. If the file is missing the hook warns and skips this one rule.
- Only **added lines** are scanned, so legacy lines in a file you merely touch do not block you; lines you add must be clean.
- A line with the marker `hm-guard: ignore` is exempt from the secret-pattern and local-path rules only (for test fixtures
  that contain fake keys). Terms and exact key values can never be ignored.
- **Allow list.** `Tools/githooks/allow.txt` lists path globs exempt from `size` and `binary-asset` only. Add an entry
  only with the owner's approval, in a commit of its own, with the reason; it takes effect from the next commit.
- Audit without staging: `python Tools/githooks/guard.py --profile unity --worktree [paths]` checks the working tree
  as if everything were about to be added. `python Tools/githooks/guard.py --profile unity --self-test` runs the decoy tests
  (65 cases). Check what a push would send without pushing:
  `printf 'refs/heads/main %s refs/heads/main %s
' $(git rev-parse main) $(git rev-parse origin/main) | python Tools/githooks/guard.py --profile unity --pre-push origin x`.
- When a commit is blocked: unstage the path (`git restore --staged <path>`), remove or genericise the content, or
  git-ignore the file. Do **not** work around the hook.
- **Emergency bypass, repository owner only:** `HM_GUARD_BYPASS="reason" git commit ...` (or `git commit --no-verify`).
  The first form prints a warning and appends the reason to `.git/hm-guard-bypass.log`. Agents never bypass the hooks.

## How workflows commit

- A **git agent** commits logical groups at the end of each workflow stage; individual workflows do not push and
  do not commit each other's files.
- Stage with **explicit path lists** (`git add <paths>`). Never `git add -A`, `git add .` or `git add -f`. Read
  `git diff --cached --stat` before every commit.
- A file that is being edited right now (modified within the last few minutes, or owned by a running workflow) is **left
  for the next checkpoint** and listed as such. A checkpoint is a snapshot: it must parse/compile and be labelled.
- Keep Unity housekeeping out of feature commits: editor-upgraded settings, package manifest and lock changes go in a `build:` commit.
- The film tooling under `Assets/Film` and the render output under `Recordings/` are handled by their own workflow;
  `Recordings/` is ignored and must never be added by force.

## Push policy

- Nothing is pushed without the owner's explicit go-ahead in the conversation. Agents prepare local commits only.
- Before the owner pushes: run the audit above, look at `git log origin/main..main` and `git diff --stat origin/main..main`,
  and make sure no private data is part of the range.
- The owner pushes with `git push origin main`. Remote branches and tags are only created, deleted or force-updated by the owner.
- Earlier history can contain material that predates these rules. It cannot be fixed by a normal commit; whether to
  rewrite it is a decision for the owner. The tip is clean of it except what is listed under "Legacy files" below.
- Before the owner pushes the 2026-10-08 series: the compile gate (see "Commit conventions") was read on 2026-10-08 from the
  running editor (`compilationFailed` false); the result and its limits are in `git notes show fdbfae6`. Recompile once more
  with the tree that is actually pushed. The audit of the 15 local commits with the `pre-push` check above (real private terms
  list) was clean.

## Legacy files

Two local-only legacy files (an editor script that builds a preview scene for a specific take, and the scene it generates)
name the dancers and absolute paths. They were tracked before these rules existed and are **untracked since 2026-10-08**
(`git rm --cached`; the working tree keeps them) and git-ignored by the patterns at the end of `.gitignore`. Their content
remains in the older, already published history, which only the owner can clean.

## Line endings

`core.autocrlf=true` is set on the development machine, so Git may print "LF will be replaced by CRLF" warnings; they are harmless.
`.gitattributes` forces LF for shell scripts and the hooks, because a CRLF shebang line breaks them.
