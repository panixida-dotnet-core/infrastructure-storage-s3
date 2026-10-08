using Microsoft.Extensions.Options;

namespace PANiXiDA.Core.Infrastructure.Storage.S3.Configuration;

internal sealed class S3StorageOptionsValidator : IValidateOptions<S3StorageOptions>
{
    public ValidateOptionsResult Validate(string? name, S3StorageOptions options)
    {
        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.BucketName))
        {
            failures.Add($"'{nameof(S3StorageOptions.BucketName)}' must be configured.");
        }

        if (options.KeyPrefix is null ||
            options.KeyPrefix != options.KeyPrefix.Trim().Trim('/') ||
            options.KeyPrefix.Contains('\\') ||
            options.KeyPrefix.Split('/').Any(segment => segment is "." or ".."))
        {
            failures.Add($"'{nameof(S3StorageOptions.KeyPrefix)}' must be empty or a relative prefix without surrounding whitespace, slashes, backslashes, or dot segments.");
        }

        if (string.IsNullOrWhiteSpace(options.AccessKey) != string.IsNullOrWhiteSpace(options.SecretKey))
        {
            failures.Add($"'{nameof(S3StorageOptions.AccessKey)}' and '{nameof(S3StorageOptions.SecretKey)}' must both be configured.");
        }

        if (options.MaxInMemoryDownloadParts <= 0)
        {
            failures.Add($"'{nameof(S3StorageOptions.MaxInMemoryDownloadParts)}' must be positive.");
        }

        if (options.PresignedUrlLifetime <= TimeSpan.Zero || options.PresignedUrlLifetime > TimeSpan.FromDays(7))
        {
            failures.Add($"'{nameof(S3StorageOptions.PresignedUrlLifetime)}' must be greater than zero and at most seven days.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
