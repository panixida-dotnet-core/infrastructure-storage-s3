namespace PANiXiDA.Core.Infrastructure.Storage.S3.Configuration;

/// <summary>
/// Configures the bucket, key namespace, credentials, and file transfer settings.
/// </summary>
public sealed class S3StorageOptions
{
    /// <summary>Gets the configuration section name.</summary>
    public const string SectionName = "S3Storage";

    /// <summary>Gets the existing bucket used for file content.</summary>
    public string BucketName { get; init; } = string.Empty;

    /// <summary>Gets an optional key prefix without leading or trailing slashes.</summary>
    public string KeyPrefix { get; init; } = string.Empty;

    /// <summary>Gets an optional access key, paired with <see cref="SecretKey"/>.</summary>
    public string AccessKey { get; init; } = string.Empty;

    /// <summary>Gets an optional secret key. Omit both keys to use the AWS credential chain.</summary>
    public string SecretKey { get; init; } = string.Empty;

    /// <summary>Gets the maximum number of download parts buffered in memory.</summary>
    public int MaxInMemoryDownloadParts { get; init; } = 4;

    /// <summary>Gets the signed URL lifetime, from more than zero to seven days.</summary>
    public TimeSpan PresignedUrlLifetime { get; init; } = TimeSpan.FromMinutes(15);
}
