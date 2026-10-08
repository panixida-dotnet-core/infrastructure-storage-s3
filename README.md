## What to do after creating a repository from this template

### 1. Rename repository metadata
- change repository name
- change solution / project names
- change package ID
- change assembly name
- change repository URLs
- change ProjectReference in test project
- update the template smoke test namespace and expected assembly name

### 2. Update package metadata
- description
- tags

### 3. Update documentation
- replace this template README with the project README
- fill all placeholder sections
- update badges
- update installation instructions
- add real usage examples

### 4. Configure GitHub repository
- check repository visibility
- configure default branch
- configure branch protection rules
- configure Issues / Discussions if needed
- configure repository description, topics and website
- register the repository in the shared SonarQube inventory

### 5. Prepare the first release
- update versioning configuration pathFilters in version.json
- verify NuGet metadata
- verify README and icon inside the package
- publish the first package version
- the version is updated automatically based on the commit history

---

# Universal README template for the NuGet package

# <PackageName>

`<PackageName>` is a .NET library for <short purpose>.

It is designed for <target audience> who need <main value / main scenario>.

## Status

[![CI](https://github.com/<OWNER>/<REPOSITORY>/actions/workflows/ci.yml/badge.svg)](https://github.com/<OWNER>/<REPOSITORY>/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/<PACKAGE_ID>.svg)](https://www.nuget.org/packages/<PACKAGE_ID>)
[![NuGet downloads](https://img.shields.io/nuget/dt/<PACKAGE_ID>.svg)](https://www.nuget.org/packages/<PACKAGE_ID>)
[![Target Framework](https://img.shields.io/badge/target-net10.0-512BD4)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/github/license/<OWNER>/<REPOSITORY>.svg)](LICENSE)

## Overview

Describe:

- what problem this package solves;
- why it exists;
- where it fits in the system or ecosystem;
- how it differs from alternatives, if that matters.

Keep this section short and practical.

## Features

- Feature 1
- Feature 2
- Feature 3
- Feature 4
- Feature 5

## Quick Start

### Requirements

- .NET 10 SDK

### Installation

```xml
<ItemGroup>
  <PackageReference Include="<PACKAGE_ID>" Version="..." />
</ItemGroup>
````

### Minimal import

```csharp
using <RootNamespace>;
```

### First example

```csharp
// Add a minimal example here
```

## Usage

### Basic usage

```csharp
// Add a basic example here
```

### Typical scenario

```csharp
// Add a realistic example here
```

### Advanced scenario

```csharp
// Add an advanced example here if needed
```

## Configuration

Describe configuration only if the package actually requires it.

Possible topics:

* environment variables;
* `appsettings.json`;
* feature flags;
* external services;
* secrets;
* runtime prerequisites.

If the package does not require runtime configuration, say so explicitly.

## Project Structure

```text
.
├── src/
│   └── <ProjectName>/
├── tests/
│   └── <ProjectName>.UnitTests/
├── .editorconfig
├── .gitattributes
├── .gitignore
├── Directory.Build.props
├── Directory.Build.targets
├── Directory.Packages.props
├── global.json
├── version.json
├── LICENSE
└── README.md
```

### Main repository files

* `src/` — source code
* `tests/` — automated tests
* `Directory.Build.props` — shared MSBuild settings
* `Directory.Build.targets` — shared build / packaging settings
* `Directory.Packages.props` — centralized package versions
* `global.json` — SDK and tooling configuration
* `version.json` — versioning configuration
* `README.md` — package overview and usage documentation

## Development

### Build

```bash
dotnet restore
dotnet build --configuration Release
```

### Format

```bash
dotnet format
```

### Test

```bash
dotnet test --configuration Release
```

### Pack

```bash
dotnet pack --configuration Release
```

### Continuous integration

Every pull request and push to `main` runs formatting, tests, and mandatory
SonarQube analysis. Publishing from `main` starts only after the SonarQube
Quality Gate succeeds.

### Full local validation

```bash
dotnet restore
dotnet format
dotnet build --configuration Release
dotnet test --configuration Release
dotnet pack --configuration Release
```

### Tooling and conventions

This repository uses:

* .NET 10
* Nullable enabled
* Implicit usings enabled
* Central package management
* GitHub Actions
* SonarQube
* Nerdbank.GitVersioning

Add more items only if they are actually relevant for the repository.

## API / Contracts / Examples

Describe the public API surface here.

Suggested structure:

* core abstractions;
* main entry points;
* key extension methods;
* important behavioral notes;
* typical integration examples.

## Roadmap / TODO

Potential future improvements:

* item 1;
* item 2;
* item 3.

Remove this section if it does not provide value.

## Contributing

Contributions are welcome.

### General rules

* keep the public API intentional;
* avoid unnecessary dependencies;
* preserve repository conventions;
* do not introduce breaking changes without review;
* keep documentation updated.

### Code style

* follow the repository `.editorconfig`;
* prefer readable and explicit code;
* keep naming consistent with the existing codebase.

### Tests

* add or update tests for meaningful behavior changes;
* cover both success and failure scenarios where applicable;
* add regression tests for bug fixes.

### Validation before completion

Run:

```bash
dotnet restore
dotnet format
dotnet build --configuration Release
dotnet test --configuration Release
```

## License

This project is licensed under the <LicenseName> license.

See the [LICENSE](LICENSE) file for details.

## Maintainers / Contacts

Maintained by <Author / Team / Organization>.

For questions or improvements, use:

* GitHub Issues
* Pull Requests
* GitHub Discussions, if enabled
