# PANiXiDA.Core.Infrastructure.Storage.S3

S3 implementation of `IFileStorage` from `PANiXiDA.Core.Application` for .NET 10.
Supports Amazon S3 and compatible providers, including custom endpoints.

## Status

[![CI](https://github.com/panixida-dotnet-core/infrastructure-storage-s3/actions/workflows/ci.yml/badge.svg)](https://github.com/panixida-dotnet-core/infrastructure-storage-s3/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/PANiXiDA.Core.Infrastructure.Storage.S3.svg)](https://www.nuget.org/packages/PANiXiDA.Core.Infrastructure.Storage.S3)
[![NuGet downloads](https://img.shields.io/nuget/dt/PANiXiDA.Core.Infrastructure.Storage.S3.svg)](https://www.nuget.org/packages/PANiXiDA.Core.Infrastructure.Storage.S3)
[![Target Framework](https://img.shields.io/badge/target-net10.0-512BD4)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/github/license/panixida-dotnet-core/infrastructure-storage-s3.svg)](LICENSE)

## Overview

This package connects the file storage contract in
[`PANiXiDA.Core.Application`](https://github.com/panixida-dotnet-core/application#file-storage)
to S3 through the AWS SDK. Register it in the application's composition root;
consumers continue to depend on `IFileStorage`.
Applications own file keys, metadata, authorization, and database coordination.

## Features

- Stream upload, download, and idempotent deletion.
- Multipart uploads and parallel ranged downloads through the AWS SDK TransferUtility.
- Presigned PUT and GET URLs with configurable expiration.
- Optional key prefix for separating environments in one bucket.
- Configuration binding, startup validation, and dependency injection.

## Quick Start

### Requirements

- .NET 10 SDK.
- An existing S3 bucket and credentials with access to the required operations.
- Docker for running integration tests.

### Installation

```bash
dotnet add package PANiXiDA.Core.Infrastructure.Storage.S3
```

### Minimal setup

Add the settings from [Configuration](#configuration), then register storage
with the host's service collection and configuration:

```csharp
using PANiXiDA.Core.Infrastructure.Storage.S3.DependencyInjection;

builder.Services.AddS3FileStorage(builder.Configuration);
```

## Usage

### Stream uploads and downloads

Consumers depend on the application contract:

```csharp
using PANiXiDA.Core.Application.Storage;

public sealed class FileContentService(IFileStorage storage)
{
    public Task UploadAsync(
        string key,
        Stream content,
        CancellationToken cancellationToken)
    {
        return storage.UploadAsync(key, content, "application/octet-stream", cancellationToken);
    }

    public async Task CopyToAsync(
        string key,
        Stream destination,
        CancellationToken cancellationToken)
    {
        await using var content = await storage.DownloadAsync(key, cancellationToken);
        await content.CopyToAsync(destination, cancellationToken);
    }
}
```

### Direct client transfers

For browser or other direct clients, authorize the operation and generate a
presigned URL through `IFileStorage`. The client uploads with HTTP PUT or downloads
with HTTP GET. After an upload, verify the stored content before marking the
application's file record ready. See [Storage behavior](#storage-behavior) for
header, expiration, and stream ownership requirements.

## Configuration

Configure an existing bucket and the AWS SDK endpoint in `appsettings.json`:

```json
{
  "AWS": {
    "ServiceURL": "https://s3.example.com",
    "AuthenticationRegion": "us-east-1",
    "ForcePathStyle": true
  },
  "S3Storage": {
    "BucketName": "files",
    "KeyPrefix": "development",
    "MaxInMemoryDownloadParts": 4,
    "DownloadPartSizeBytes": 8388608,
    "PresignedUrlLifetime": "00:15:00"
  }
}
```

Provide credentials through `S3Storage__AccessKey` and `S3Storage__SecretKey`
environment variables or a secret configuration provider. Omit both to use the
standard AWS credential chain. For Amazon S3, use `AWS:Region` instead of a custom
`ServiceURL` and `AuthenticationRegion`.

`S3StorageOptions` binds to the required `S3Storage` section and is validated at startup:

| Setting | Default | Requirement |
| --- | --- | --- |
| `BucketName` | Empty | An existing bucket name is required. |
| `KeyPrefix` | Empty | Optional relative prefix without surrounding whitespace, leading or trailing slashes, backslashes, or dot segments. |
| `AccessKey` / `SecretKey` | Empty | Supply both or use the AWS credential chain. |
| `DownloadPartSizeBytes` | `8388608` (8 MiB) | Must be positive. |
| `MaxInMemoryDownloadParts` | `4` | Must be positive. |
| `PresignedUrlLifetime` | `00:15:00` | Greater than zero and at most seven days. |

The package does not create buckets or configure their access policies.
Browser clients also require a suitable bucket CORS policy.

## Project Structure

```text
.
├── src/
│   └── PANiXiDA.Core.Infrastructure.Storage.S3/
│       ├── Configuration/
│       ├── DependencyInjection/
│       └── S3FileStorage.cs
├── tests/
│   ├── PANiXiDA.Core.Infrastructure.Storage.S3.UnitTests/
│   └── PANiXiDA.Core.Infrastructure.Storage.S3.IntegrationTests/
├── .github/workflows/ci.yml
├── .editorconfig
├── .gitattributes
├── .gitignore
├── Directory.Build.props
├── Directory.Build.targets
├── Directory.Packages.props
├── global.json
├── version.json
├── icon.png
├── LICENSE
└── README.md
```

### Main repository files

- `src/` contains the adapter, options, and dependency injection registration.
- `tests/` contains unit tests and container-based S3 integration tests.
- `Directory.Build.props` defines shared build settings; `Directory.Build.targets`
  defines NuGet metadata and includes the README and icon in the package.
- `Directory.Packages.props` centralizes dependency versions.
- `global.json` selects the .NET SDK and Microsoft.Testing.Platform runner.
- `version.json` configures Nerdbank.GitVersioning and package version inputs.

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

Unit tests cover validation, SDK request mapping, error handling, cancellation,
and registration. Integration tests require Docker and start
[SeaweedFS](https://github.com/seaweedfs/seaweedfs) `4.48` through Testcontainers.
They exercise real S3 requests, prefixes, presigned HTTP uploads and downloads,
and rejection of modified signed requests. Transfer tests verify single PUT below
the 16 MiB threshold, multipart requests at and above it, ranged downloads exceeding
the part buffer limit, and SHA-256 hashes for small and large files. Each test uses a unique
prefix; disposing the container removes its data. No external S3 credentials are needed.

### Pack

```bash
dotnet pack --configuration Release
```

### Continuous integration

Every pull request and push to `main` runs formatting, tests, coverage checks, and
mandatory SonarQube analysis. Publishing from `main` requires the SonarQube Quality Gate to pass.

### Full local validation

```bash
dotnet restore
dotnet format
dotnet build --configuration Release
dotnet test --configuration Release
dotnet pack --configuration Release
```

### Tooling and conventions

The repository uses .NET 10 with nullable reference types, implicit usings, and
central package management. Tests use Microsoft.Testing.Platform, xUnit v3,
Shouldly, NSubstitute, and Testcontainers. GitHub Actions runs CI and SonarQube;
Nerdbank.GitVersioning generates package versions.

## API Overview

| Entry point | Namespace | Purpose |
| --- | --- | --- |
| `AddS3FileStorage` | `PANiXiDA.Core.Infrastructure.Storage.S3.DependencyInjection` | Registers scoped `IFileStorage`, singleton AWS client and transfer utility, and validated options. |
| `S3StorageOptions` | `PANiXiDA.Core.Infrastructure.Storage.S3.Configuration` | Configures bucket, prefix, credentials, download buffering, and URL lifetime. |
| `IFileStorage` | `PANiXiDA.Core.Application.Storage` | Application contract for stream operations and presigned URLs, provided by Core.Application. |
| `PresignedUploadUrl` / `PresignedDownloadUrl` | `PANiXiDA.Core.Application.Storage.Models` | URL results provided by Core.Application. |

### Storage behavior

Keys are case-sensitive, relative paths generated and persisted by the application.
The adapter prepends `KeyPrefix`: `files/avatar.png` becomes
`development/files/avatar.png`. Empty keys, leading slashes, backslashes, and `.` or
`..` path segments are rejected. Prefixes are logical namespaces; access policies
must enforce any required isolation.

Uploads read from the stream's current position, leave it open, and replace existing
content at the same key. The caller disposes download streams and passes cancellation
tokens to subsequent reads. Missing objects raise `FileNotFoundException`; other
storage failures propagate. Deleting an absent object succeeds.
`UploadAsync` and `DeleteAsync` validate arguments synchronously
before returning the SDK task.

Downloads use parallel HTTP Range requests. The product of `DownloadPartSizeBytes`
and `MaxInMemoryDownloadParts` estimates part buffering per active download:
32 MiB with defaults, plus SDK and HTTP overhead.
Account for simultaneous downloads when sizing these limits.
If opening a ranged download fails with HTTP 416, the adapter retries once
with a regular GET, supporting providers that reject ranged or part GETs for empty files.

`GetPresignedUploadUrlAsync` signs an HTTP PUT for the declared content type and size.
Send the returned `RequiredHeaders` and the exact content length. The HTTP client
normally sets content length from the upload body. `GetPresignedDownloadUrlAsync`
signs an HTTP GET with the suggested file name and response content type; no extra
request headers are needed. Use URLs unchanged. Credentials can expire or be revoked
before the configured URL expiration.

URL generation does not transfer content or check whether the object exists.

## Contributing

### General rules

Keep the public API small, preserve existing contracts, avoid unnecessary dependencies,
and update documentation when behavior changes. Breaking changes require review.

### Code style

Follow [`.editorconfig`](.editorconfig) and the repository conventions in
[`AGENTS.md`](AGENTS.md). Prefer explicit code and consistent naming.

### Tests

Cover meaningful behavior changes with success and failure cases; add regression
tests for bug fixes. Integration tests must use the disposable container rather
than require credentials for an external account.

### Validation before completion

Run [full local validation](#full-local-validation) with Docker running before
submitting a pull request.

## License

This project is licensed under the Apache-2.0 license.
See the [LICENSE](LICENSE) file for details.

## Maintainers / Contacts

Maintained by PANiXiDA. For questions or improvements, use
[GitHub Issues](https://github.com/panixida-dotnet-core/infrastructure-storage-s3/issues)
or [Pull Requests](https://github.com/panixida-dotnet-core/infrastructure-storage-s3/pulls).
