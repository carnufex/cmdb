# CLAUDE.md

Instructions for agents (Claude Code) working in this repository.

## Absolute rules

1. **Synthetic data, always.** Never commit real data, real data models or the names of real organisations, systems or people. Unsure: stop and ask on the issue.
2. **All work is done against an issue.** If there is none, create it first (`gh issue create`, with the right template) and then work against it.
3. **Architecture decisions are not made silently.** A deviation from an ADR or a new decision needs a new ADR with status *Proposed* and an issue labelled `type:decision` and `needs-human`.
4. **The performance budget in [docs/plan.md](docs/plan.md) is a requirement.** Code that breaks it is not done.
5. **Authorisation is never bypassed**, not in test, debug or export paths either.

## Workflow

1. **Pick an issue.** Preferably one with `status:ready` and no `needs-human`. Read the issue, linked issues, relevant ADRs and documents in `docs/`.
2. **Claim the issue:** `gh issue edit <nr> --add-assignee @me --add-label status:in-progress --remove-label status:ready`.
3. **Comment a plan** on the issue before implementing, when the work is more than a small change: approach, affected slices, test plan and open questions.
4. **Branch:** `<nr>-short-slug`, for example `12-trace-service`.
5. **Commits:** Conventional Commits with the issue number, for example `feat(graph): trace across layers (#12)`.
6. **Document as you work.** Comment decisions, surprises and measurements on the issue. The issue must be enough to understand why the code looks the way it does.
7. **Discovered work outside the scope** becomes a new issue, linked to the current one with "Discovered in #<nr>". Do not widen the scope silently.
8. **Blocked?** Set `status:blocked` and describe what is needed. When a human decision is needed, also set `needs-human`.
   `needs-human` may **only** be set together with a section `## What is needed from you` on the issue: a concrete question or action, the options (with your recommendation) and how to answer, for example "comment A or B". If you can find the answer yourself, in this repository, in the homelab repository or in the documentation, do that instead. Remove the label when the answer is there.
9. **Pull request** with `Closes #<nr>`, a description of how it was verified, performance numbers where relevant, and screenshots if any.
10. **Update the documentation** in `docs/` when behaviour or the model changes.
11. **Merge and deploy.** When `scripts/verify.sh` is green: squash-merge the PR and run `scripts/deploy.sh --deploy --wait` from a clean `main`. Verify the change on https://cmdb.rosenvall.se and comment the deployed tag on the issue. Follow [docs/drift.md](docs/drift.md).

The flow runs with the `delivery@carnufex` plugin (enabled in `.claude/settings.json`): the skills `new-issue`, `work-issue`, `plan-milestone`, `decision` and `ship` (everything from PR to the demo environment). cmdb's own facts for them (labels, verification, deployment, troubleshooting) are in `.claude/profiles/delivery.md`; change facts there and the method in [carnufex/claude-skills](https://github.com/carnufex/claude-skills).

## Parallel work

Several sessions work in this repository at the same time. If the main checkout has changes you did not make, leave them alone and work in a worktree (`git worktree add ../cmdb-<nr> <nr>-slug`). Stage explicit paths, never `git add -A`, and run `git fetch` before every push.

## Conventions

- **Language:** everything written for the work is in English: code, identifiers, comments, commit messages, documentation, ADRs, issues, PR descriptions and issue comments, whatever language the request was in. Existing Swedish documents stay until they are next changed; whoever changes one writes the new text in English. User-facing text in the app's Swedish locale stays Swedish.
- **Backend:** .NET 10, FastEndpoints, vertical slices (one folder per feature with endpoint, request/response, validation, handler and test). No generic "services" layers.
- **Frontend:** Angular with standalone components and signals. All styling through design tokens. Status is shown with a dot and text.
- **Database:** EF Core code-first (ADR-0009). The schema changes only through the model and `dotnet ef migrations add`, never directly in the database. Bulk paths may use Npgsql/`COPY` against the EF schema. Geometry in EPSG:3006.
- **Tests:** unit tests for domain logic, integration tests against a real Postgres (Testcontainers), benchmarks for the graph engine.
- **Containers:** OpenShift compatible (non-root, arbitrary UID).

## Local environment

```bash
scripts/init-env.sh
docker compose up -d
```
