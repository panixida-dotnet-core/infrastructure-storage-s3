# PANiXiDA.Core.Infrastructure.Storage.S3

S3 implementation of `IFileStorage` from `PANiXiDA.Core.Application` for .NET 10.
Supports Amazon S3 and compatible providers, including custom endpoints.

## Status

[![CI](https://github.com/panixida-dotnet-core/infrastructure-storage-s3/actions/workflows/ci.yml/badge.svg)](https://github.com/panixida-dotnet-core/infrastructure-storage-s3/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/PANiXiDA.Core.Infrastructure.Storage.S3.svg)](https://www.nuget.org/packages/PANiXiDA.Core.Infrastructure.Storage.S3)
[![NuGet downloads](https://img.shields.io/nuget/dt/PANiXiDA.Core.Infrastructure.Storage.S3.svg)](https://www.nuget.org/packages/PANiXiDA.Core.Infrastructure.Storage.S3)
[![Target Framework](https://img.shields.io/badge/target-net10.0-512BD4)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/github/license/panixida-dotnet-core/infrastructure-storage-s3.svg)](LICENSE)

## Features

- Stream upload, download, and idempotent deletion.
- Multipart uploads and range downloads through the AWS SDK TransferUtility.
- Presigned PUT and GET URLs with configurable expiration.
- Optional key prefix for separating environments in one bucket.
- Configuration binding, startup validation, and dependency injection.

## Quick Start

```bash
dotnet add package PANiXiDA.Core.Infrastructure.Storage.S3
```

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
    "PresignedUrlLifetime": "00:15:00",
    "MaxInMemoryDownloadParts": 4
  }
}
```

Provide credentials through `S3Storage__AccessKey` and `S3Storage__SecretKey`
environment variables or a secret configuration provider. Omit both to use the
standard AWS credential chain. For Amazon S3, use `AWS:Region` instead of a custom
`ServiceURL` and `AuthenticationRegion`.

Register storage in the application:

```csharp
using PANiXiDA.Core.Infrastructure.Storage.S3.DependencyInjection;

builder.Services.AddS3FileStorage(builder.Configuration);
```

Consumers depend on the application contract:

```csharp
using PANiXiDA.Core.Application.Storage;

public sealed class FileContentService(IFileStorage storage)
{
    public Task UploadAsync(string key, Stream content, CancellationToken cancellationToken)
    {
        return storage.UploadAsync(key, content, "application/octet-stream", cancellationToken);
    }

    public async Task CopyToAsync(string key, Stream destination, CancellationToken cancellationToken)
    {
        await using var content = await storage.DownloadAsync(key, cancellationToken);
        await content.CopyToAsync(destination, cancellationToken);
    }
}
```

## Storage Behavior

Keys are case-sensitive, relative paths generated and persisted by the application.
The adapter prepends `KeyPrefix`: `files/avatar.png` becomes
`development/files/avatar.png`. Empty keys, leading slashes, backslashes, and `.` or
`..` path segments are rejected. Prefixes are logical namespaces; access policies
must enforce any required isolation.

Uploads read from the stream's current position, leave it open, and replace existing
content at the same key. The caller disposes download streams and passes cancellation
tokens to subsequent reads. Missing objects raise `FileNotFoundException`; other
storage failures propagate. Deleting an absent object succeeds.

`GetPresignedUploadUrlAsync` signs an HTTP PUT for the declared content type and size.
Send the returned `RequiredHeaders` and the exact content length. The HTTP client
normally sets content length from the upload body. `GetPresignedDownloadUrlAsync`
signs an HTTP GET with the suggested file name and response content type; no extra
request headers are needed. Use URLs unchanged. URL lifetime defaults to 15 minutes
and must be greater than zero and at most seven days; credentials can expire earlier.

URL generation does not transfer content or check whether the object exists.
Applications authorize access, verify uploaded content, and manage metadata and
coordination with database transactions. Browser clients also require a suitable
bucket CORS policy.

## Development

```bash
dotnet restore
dotnet format
dotnet build --configuration Release
dotnet test --configuration Release
dotnet pack --configuration Release
```

Unit tests cover validation, SDK request mapping, error handling, cancellation,
and registration. Integration tests require Docker and start
[SeaweedFS](https://github.com/seaweedfs/seaweedfs) `4.48` through Testcontainers.
They exercise real S3 requests, multipart transfers, prefixes, presigned HTTP uploads
and downloads, and rejection of modified signed requests. Each test uses a unique
prefix; disposing the container removes its data. No external S3 credentials are needed.

Every pull request and push to `main` runs formatting, tests, coverage checks, and
SonarQube analysis. Publishing from `main` requires the SonarQube Quality Gate to pass.
Versions are generated by Nerdbank.GitVersioning.

## License

[Apache License 2.0](LICENSE).
