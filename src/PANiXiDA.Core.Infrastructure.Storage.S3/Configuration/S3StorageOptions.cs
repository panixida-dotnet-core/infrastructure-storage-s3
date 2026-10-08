namespace PANiXiDA.Core.Infrastructure.Storage.S3.Configuration;

/// <summary>
/// Configures the bucket, key namespace, credentials, and file transfer settings.
/// </summary>
public sealed class S3StorageOptions
{
    /// <summary>Gets the configuration section name.</summary>
    public const string SectionName = "S3Storage";

    /// <summary>Gets or sets the existing bucket used for file content.</summary>
    public string BucketName { get; set; } = string.Empty;

    /// <summary>Gets or sets an optional key prefix without leading or trailing slashes.</summary>
    public string KeyPrefix { get; set; } = string.Empty;

    /// <summary>Gets or sets an optional access key, paired with <see cref="SecretKey"/>.</summary>
    public string AccessKey { get; set; } = string.Empty;

    /// <summary>Gets or sets an optional secret key. Omit both keys to use the AWS credential chain.</summary>
    public string SecretKey { get; set; } = string.Empty;

    /// <summary>Gets or sets the size of each ranged download part in bytes.</summary>
    public long DownloadPartSizeBytes { get; set; } = 8 * 1024 * 1024;

    /// <summary>Gets or sets the maximum number of download parts buffered in memory per stream.</summary>
    public int MaxInMemoryDownloadParts { get; set; } = 4;

    /// <summary>Gets or sets the signed URL lifetime, from more than zero to seven days.</summary>
    public TimeSpan PresignedUrlLifetime { get; set; } = TimeSpan.FromMinutes(15);
}
