# AGENTS.md

## Purpose

This repository is a reference template for PANiXiDA .NET NuGet packages.
The goal is to keep package structure, CI, versioning, tests, documentation, and release flow consistent across repositories.

## Repository layout

- `src/` — package source code
- `tests/` — automated tests
- `README.md` — package overview and usage examples
- `Directory.Build.props` — shared build settings
- `Directory.Packages.props` — centralized package versions
- `version.json` — Nerdbank.GitVersioning configuration
- `ci.yml` — CI pipeline

## Technology baseline

- .NET 10
- Nullable enabled
- Implicit usings enabled
- Central package management enabled
- Microsoft Testing Platform
- xUnit v3
- Shouldly
- Nerdbank.GitVersioning

## Coding rules

- Preserve existing naming.
- Prefer explicit, readable code over short clever code.
- Public APIs must include XML documentation written in English.
- Avoid breaking public API changes unless explicitly requested.
- Do not weaken nullability annotations.
- Do not silently change exception behavior.

## Library design rules

- Keep public API small and intentional.
- Favor immutability for public models.
- Validate all public method arguments.
- Prefer deterministic behavior and stable error messages.
- Do not add dependencies without a strong reason.

## Tests

- Add or update tests for every meaningful behavior change.
- Cover happy path, guard clauses, and failure scenarios.
- Verify public API behavior, not implementation details, unless required.
- When fixing a bug, add a regression test first.
- Do not add `using Xunit;` or `using Shouldly;` in test files, because they are provided as global usings in the test project.
- Write `DisplayName` values in English.
- Structure tests using the Arrange, Act, Assert pattern.

## Documentation

- Update `README.md` when public behavior, public API, package metadata, or development workflow changes.
- Keep the README structure aligned with the repository standard.
- Keep installation instructions, target framework, badges, and package information accurate.
- Keep all examples compilable and aligned with the current API.
- Ensure usage examples reflect real and recommended library usage.
- Remove outdated or duplicate documentation when updating existing sections.

## Build and validation

Before considering work complete, run:

- `dotnet restore`
- `dotnet format`
- `dotnet build --configuration Release`
- `dotnet test --configuration Release`

## Continuous integration

- SonarQube analysis is mandatory for every pull request and push to `main`.
- The `sonar` job must run after tests, and `publish` must depend on the
  successful SonarQube Quality Gate.
- Do not make SonarQube conditional or allow its failures to be ignored.

## Packaging and versioning

- Package metadata must stay complete and consistent.
- README and icon must be included in the package.
- Changes affecting package contents must be reflected in versioning inputs.
- Do not change package id, repository url, or license without explicit request.

## Definition of done

A change is done only when:

- code is formatted,
- code builds,
- tests pass,
- relevant tests were added or updated,
- README was updated if public behavior changed,
- no unnecessary files or dependencies were introduced.
