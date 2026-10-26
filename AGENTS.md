# AGENTS.md

## Purpose

Read `README.md` for setup and `ARCHITECTURE.md` before structural changes.

## Task Execution

- Follow the user's request precisely and complete every requirement within the authorized scope.
- Do not omit requirements, do not silently reduce scope and do not skip necessary work to save effort.
- Do not replace feasible work with plans, stubs, TODOs or toy examples.
- For large changes, work in coherent dependency groups: update the owning implementation and affected consumers together, and track remaining requirements.

## Before Editing

- Inspect the working tree and preserve unrelated user changes.
- Trace the affected behavior end to end: entry points, callers, implementation dependencies, configuration, persistence, and consumers, where relevant.
- Search repository-wide for related symbols and equivalent implementations. Read relevant code, tests, and documentation.
- Establish the root cause for bugs or the correct extension point for features. Verify assumptions from code, not names alone.
- For nontrivial changes, briefly state the findings, affected paths, and validation plan before editing.
- Keep investigation broad enough to establish impact, but keep implementation within the requested scope.
- Obtain approval for destructive, externally visible, or materially broader work unless already explicitly authorized.

## Fix the Source, Not the Symptoms

- Change the authoritative implementation in the layer that owns the behavior.
- Fix shared causes there and update affected callers consistently.
- Never append replacement definitions, shadow functions, or use production monkey patches, wrapper/subclass overrides, or alternate execution paths to bypass the implementation that needs fixing.
- Extend existing workflows. Introduce an abstraction only for a distinct responsibility, not as a parallel version of existing behavior.
- Do not add ad-hoc input exceptions, silent fallbacks, swallowed errors, placeholders, hardcoded sample outputs, or fabricated success to conceal a defect. Report blockers.
- Prefer the smallest complete correction, not merely the smallest diff. Remove superseded code within scope after checking references, exports, dynamic hooks, and package metadata; preserve supported public import paths.

## Rules

- Preserve existing behavior outside the user's requested scope.
- Discovering an inconsistency does not authorize a scope changing.
- Any necessary expansion of scope requires explicit user approval before modifying the implementation or tests that protect that behavior.
- When removing functionality from code or documentation or test, delete obsolete references and simplify the remaining content; describe only the current behavior, without explaining the removal or mentioning what is no longer done.


## Implementation Rules

- Separate domain logic from presentation and transport concerns. Respect dependency direction, avoid circular imports, pass dependencies explicitly, and keep types consistent across boundaries.
- Maintain one authoritative owner and suitable internal representation for each logical concept, state, or configuration value. Pass it directly between internal producers and consumers instead of reconstructing equivalent objects; retain distinct boundary formats, snapshots, and derived caches when their contracts require them.
- Keep modules and functions cohesive. Prefer in-place simplification; extract helpers for meaningful domain operations, reuse, algorithms, or resource management, not merely to forward or rename data.
- Use classes for meaningful state, invariants, or resources; prefer functions for stateless work. Do not centralize unrelated responsibilities in a universal context, catch-all model, or oversized shared module.
- Simplify actual work and end-to-end flow: remove unnecessary handoffs and data round trips, merge compatible passes, build related derived values together, and eliminate redundant scans, conversions, validations, and recomputation. Do not merely relocate complexity or worsen algorithmic cost to reduce line count.
- Keep locals that clarify meaningful logic, prevent repeated work, or preserve evaluation order, snapshots, resource lifetime, or accumulated state. Remove aliases and disposable intermediates only when direct use remains clearer and equivalent.
- Do not duplicate equivalent resource-management or transformation blocks. Consolidate them when their entry order, exit order, cleanup timing, exceptions, and externally visible side effects remain identical; otherwise keep the necessary phases explicit rather than introducing a deceptive simplification.
- Preserve public interfaces, data shapes, field names, types, contracts, outputs, side effects, error behavior, mutation, laziness, and observable ordering while simplifying. Change private interfaces only when affected consumers remain compatible or are updated within scope. Do not add indirection merely to make a reduction appear smaller.
- Respect the project's supported language, runtime, and toolchain versions.
- Prefer existing project utilities and suitable built-in capabilities.
- Validate untrusted inputs and filesystem paths at system boundaries. Resolve configuration defaults and precedence at their owner; preserve reload behavior and required validation/normalization order. Remove downstream checks only when every path establishes the same invariant, nothing invalidates it, and required errors and side effects remain unchanged. Handle specific failure conditions and expose actionable errors.
- Keep synchronous and asynchronous boundaries explicit, and avoid blocking concurrent execution with long-running work.
- Keep initialization explicit. Loading or constructing a component must not implicitly trigger network calls, downloads, service startup, heavyweight work, or filesystem mutations.

## Dependencies, Configuration and Security

- Reuse existing configuration and dependencies. Justify additions, update the owning manifest and lockfile, and separate runtime from development dependencies.
- Never expose or commit secrets or real personal data. Keep model weights, logs, caches, generated indexes, and runtime state out of source control.
- Use placeholders in configuration examples, not credentials or machine-local values.

## Testing

- Reuse the standard `build/` output and incremental intermediates for application builds. Do not create dated, versioned, or task-specific build copies for routine validation.
- Remove obsolete generated builds after validation. When an isolated build is necessary, use a stable reusable location and clean it up when finished; preserve the current runnable build, useful incremental caches, and unrelated user files.
- Prefer incremental builds and focused checks. Use a full clean/rebuild only when required by the change or to diagnose stale output, keeping build time and disk usage bounded.
- Add or update tests for behavior changes and regression fixes in supported code; avoid redundant or overlapping coverage.
- Keep permanent tests focused on current code and supported behavior. Remove obsolete tests, fixtures, and helpers with the code they cover; do not create or retain tests that only assert that removed functionality, symbols, files, or previous implementations are absent. Use temporary checks for removal verification and clean them up after validation.
- Use /tests as the main test folder.
- Test the normal execution path, not a test-only workaround.
- Use small fakes at external boundaries; tests must be deterministic and isolated and must not require live provider APIs, credentials, network access, model downloads, or machine-local model files.
- Use the test, type-check, lint, and build commands and environments documented by the current project files; do not assume independent packages share one environment. Establish the relevant baseline before nontrivial changes and distinguish pre-existing failures from regressions.
- Never weaken assertions, skip failing tests, or disable checks to hide failures. Never report a test as passing unless the command was actually run successfully.
- Rerun affected checks after the final meaningful change; earlier results do not validate subsequent edits.
- Review the final diff for contract changes and search for stale references, obsolete representations, and dead paths after consolidation.
- Keep temporary files in test-managed or ignored locations and clean them up.

## Documentation

- Update `README.md` for changes to setup, commands, configuration, or public usage.
- Update `ARCHITECTURE.md` for changes to components, ownership, boundaries, dependencies, or storage design.
- Update affected documentation and executable examples in the same code change.
- Structure `CHANGELOG.md` with `# Changelog`, then `## [Unreleased]`, followed by release sections newest first using `## [<version or tag>] - YYYY-MM-DD` and the project's versioning scheme.
- Write compact bullet entries as short, focused phrases: one user-facing change per bullet, related changes combined, no implementation detail, repetition, or filler. Use `### Added`, `### Changed`, `### Fixed`, `### Removed`, or `### Security` only when grouping improves readability; omit empty categories.
- Preserve released entries. When preparing each release, move pending notes into the matching release section and keep `Unreleased` at the top for future changes.
- Keep the documentation clean, concise, and well-organized, avoiding unnecessary bloat.
- Use package documentation for local details and cross-link shared concepts rather than duplicating them. Keep implementation details out of this file.

## Handoff

At the end of the task, report:

- Files changed and the resulting behavior.
- Tests or validation commands actually run and their observed results.
- Specific blockers, unmet requirements, and incomplete or unverified work, when applicable.

## Project-Specific Rules

- Use supported Python syntax and standard-library facilities when they are a clear fit.
- Add type annotations where they clarify contracts; do not weaken types, add unchecked casts, or suppress checks to hide incompatible changes.
- Add and update comments in functions and classes to document their purpose.
- Catch exceptions only when meaningful; use contextual, domain-scoped exceptions rather than one type per case. Preserve context when translating between domains or to user-facing errors.
- Use `pathlib` and safe path validation for filesystem operations.
