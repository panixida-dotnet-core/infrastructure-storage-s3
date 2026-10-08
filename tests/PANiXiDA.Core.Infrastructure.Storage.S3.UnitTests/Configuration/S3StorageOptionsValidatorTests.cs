using PANiXiDA.Core.Infrastructure.Storage.S3.Configuration;

namespace PANiXiDA.Core.Infrastructure.Storage.S3.UnitTests.Configuration;

public sealed class S3StorageOptionsValidatorTests
{
    private readonly S3StorageOptionsValidator _validator = new();

    [Theory(DisplayName = "An existing bucket with an optional relative prefix is valid")]
    [InlineData("")]
    [InlineData("development")]
    [InlineData("production/files")]
    public void ValidOptions(string prefix)
    {
        var options = new S3StorageOptions { BucketName = "files", KeyPrefix = prefix };

        _validator.Validate(null, options).Succeeded.ShouldBeTrue();
        options.PresignedUrlLifetime.ShouldBe(TimeSpan.FromMinutes(15));
        options.MaxInMemoryDownloadParts.ShouldBe(4);
    }

    [Theory(DisplayName = "A bucket name is required")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void InvalidBucket(string? bucket)
    {
        var result = _validator.Validate(null, new S3StorageOptions { BucketName = bucket! });

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldContain(message => message.Contains(nameof(S3StorageOptions.BucketName)));
    }

    [Theory(DisplayName = "Prefixes cannot escape or change their namespace")]
    [InlineData(null)]
    [InlineData("/development")]
    [InlineData("development/")]
    [InlineData(" development")]
    [InlineData("development ")]
    [InlineData("development\\files")]
    [InlineData("development/./files")]
    [InlineData("development/../files")]
    public void InvalidPrefix(string? prefix)
    {
        var result = _validator.Validate(null, new S3StorageOptions { BucketName = "files", KeyPrefix = prefix! });

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldContain(message => message.Contains(nameof(S3StorageOptions.KeyPrefix)));
    }

    [Theory(DisplayName = "Explicit credentials must be configured together")]
    [InlineData("access", "secret", true)]
    [InlineData("", "", true)]
    [InlineData("access", "", false)]
    [InlineData("", "secret", false)]
    public void CredentialPair(string accessKey, string secretKey, bool valid)
    {
        var options = new S3StorageOptions { BucketName = "files", AccessKey = accessKey, SecretKey = secretKey };

        _validator.Validate(null, options).Succeeded.ShouldBe(valid);
    }

    [Theory(DisplayName = "Download buffering requires a positive part count")]
    [InlineData(0)]
    [InlineData(-1)]
    public void InvalidPartCount(int parts)
    {
        var options = new S3StorageOptions { BucketName = "files", MaxInMemoryDownloadParts = parts };

        _validator.Validate(null, options).Failed.ShouldBeTrue();
    }

    [Theory(DisplayName = "Signed URL lifetime is limited to seven days")]
    [InlineData(-1, false)]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(604800, true)]
    [InlineData(604801, false)]
    public void UrlLifetime(int seconds, bool valid)
    {
        var options = new S3StorageOptions { BucketName = "files", PresignedUrlLifetime = TimeSpan.FromSeconds(seconds) };

        _validator.Validate(null, options).Succeeded.ShouldBe(valid);
    }
}
