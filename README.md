# Basalt

[![ci](https://github.com/borisarm/Basalt/actions/workflows/ci.yml/badge.svg)](https://github.com/borisarm/Basalt/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

**Basalt** is a local-first Markdown note-taking app compatible with [Obsidian](https://obsidian.md) vaults, built with **.NET** and **Avalonia** for **Windows, macOS and iOS**. Each vault syncs to **its own GitHub repository**, so you get full version history with no servers to run.

> **🤖 100 % spec-driven AI development.** Every line of code, every test and every architecture decision record in this repository is written by an AI coding agent ([Claude Code](https://claude.com/claude-code)) working from the specification in [`docs/specs/`](docs/specs/README.md). The specification is the single source of truth. See [How this project is built](#how-this-project-is-built).

> **Status: early development.** The architecture, test infrastructure and CI are in place, and the features are being built phase by phase following [the implementation plan](docs/specs/10-plan-de-implementacion.md). See [`PROGRESS.md`](PROGRESS.md) for the current state. Development targets Windows first. macOS and iOS are built in CI.

## Features (planned for the MVP)

- **Vaults**: plain folders of Markdown files that open unchanged in Obsidian and the other way round. You can keep several vaults, each with its own settings.
- **Editor**: source mode with Markdown highlighting, a rendered reading mode, autosave, list continuation and find/replace.
- **Links**: `[[wikilinks]]`, embeds `![[...]]`, aliases, heading and block links, autocomplete, and link updates when you rename or move notes.
- **Knowledge tools**: backlinks and unlinked mentions, nested `#tags`, frontmatter properties, outline, bookmarks, and a global and local graph view.
- **Search**: full-text search (SQLite FTS5) with Obsidian-style operators (`path:`, `file:`, `tag:`, `line:`, `section:`, `-`, `OR`, quotes, `/regex/`), a quick switcher and a command palette.
- **Productivity**: daily notes and templates (`{{date:YYYY-MM-DD}}`, `{{title}}`…).
- **GitHub sync per vault**:
  - one repository and one branch per vault
  - OAuth device-flow sign-in, with support for several accounts
  - manual or automatic sync, with independent policies per vault
  - three-way merges, conflict copies, a conflict center and per-note version history
  - uses the GitHub Git Data API directly, with no local `.git` folder, so it works the same way on iOS

The guiding rule is **never lose user data**: whenever there is doubt, Basalt keeps both versions.

## Architecture

Basalt is a **hexagonal (ports & adapters) modular monolith**:

```
Basalt.UI (Avalonia views) → Basalt.Presentation (ViewModels, no Avalonia)
                     │ use cases (input ports)
             Basalt.Application  ── output ports ──▶  Adapters: FileSystem · Markdown (Markdig)
                     │                                          Index (SQLite/FTS5) · GitHub (REST)
               Basalt.Domain (pure)                             Platform (Windows / Apple)
```

- `Basalt.Domain` is pure. It has no I/O, no clock, no randomness and no NuGet packages. Link resolution, search parsing, diff/merge and the sync planner all live here.
- `Basalt.Application` contains the use cases and the port interfaces, and only knows about ports.
- Adapters are `internal`. Each one exposes only its DI registration extension.
- Architecture tests and banned-API analyzers enforce these dependency rules.

The details are in the [specification](docs/specs/README.md), which is written in Spanish, and in the [architecture decision records](docs/specs/adr/README.md).

## How this project is built

Basalt is an experiment in **100 % spec-driven AI development**. The code is not written by hand. An AI coding agent builds it entirely from a written specification:

1. **The specification decides.** [`docs/specs/`](docs/specs/README.md) describes the product: vision, architecture, domain model, ports, Gherkin scenarios, sync algorithm, UI, persistence, test strategy, non-functional requirements and an ordered [implementation plan](docs/specs/10-plan-de-implementacion.md). If the code and the specification disagree, the code is fixed.
2. **The agent works task by task.** Following [`CLAUDE.md`](CLAUDE.md), the agent takes the next unfinished task in the plan, reads the referenced sections and implements it with strict TDD:
   - write the tests first and watch them fail
   - tag every test with the scenario it covers (`[Trait("Spec", "F-007.2")]`)
   - write the minimum code to make them pass
3. **Gaps become ADRs.** When the specification is ambiguous, the agent takes the most conservative option (the one that never loses data), records it in an [ADR](docs/specs/adr/README.md) and updates the specification in the same commit.
4. **Machines enforce quality.** Each task meets the same Definition of Done before it is committed:
   - a warning-free build
   - all tests green
   - formatting, coverage and mutation thresholds
   - architecture tests
5. **Progress is visible.** Each task ends with one Conventional Commit, an entry in [`PROGRESS.md`](PROGRESS.md) and a push that CI verifies on Windows, macOS and Linux.

## Repository layout

| Path | Contents |
|------|----------|
| `src/` | Production projects: Domain, Application, Adapters.\*, Presentation, UI, Composition, Desktop |
| `tests/` | Unit, contract, integration, UI (headless), architecture and sync simulation tests, plus shared fakes in `Basalt.Testing` and a fake GitHub server |
| `docs/specs/` | Product and system specification, plan and ADRs |
| `build/` | Coverage gate and test scripts used by CI |
| `.github/workflows/` | CI (`ci.yml`) and nightly (`nightly.yml`) pipelines |

## Building and testing

Requirements:

- .NET SDK 11 (the version is pinned in `global.json`)
- the .NET 10 runtime, because the projects target `net10.0`

```bash
dotnet build Basalt.slnx
dotnet test --solution Basalt.slnx                              # all tests
dotnet test --project tests/Basalt.Domain.Tests                 # one project
dotnet test --project tests/Basalt.Domain.Tests -- --filter-trait "Spec=SYNC-ROW-10"
bash build/cover.sh tests/Basalt.Domain.Tests                   # coverage + gate
dotnet format --verify-no-changes
dotnet run --project src/Basalt.Desktop
```

Development is test-driven. Every specified scenario is traced to tests with `[Trait("Spec", "<id>")]`. CI enforces coverage thresholds of at least 95 % lines on the domain and at least 90 % on the application layer, and runs mutation testing nightly.

## Contributing

Read [`CLAUDE.md`](CLAUDE.md) and the specification before contributing. In short:

- write the tests first
- follow the [code conventions](docs/specs/13-convenciones-de-codigo.md)
- make one Conventional Commit per task
- keep the build warning-free

## License

[MIT](LICENSE) © 2026 Boris Armenta
