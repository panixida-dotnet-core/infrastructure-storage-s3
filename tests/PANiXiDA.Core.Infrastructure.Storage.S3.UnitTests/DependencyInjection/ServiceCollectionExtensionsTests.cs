using System.Globalization;
using System.Web;
using Amazon.Extensions.NETCore.Setup;
using Amazon.S3;
using Amazon.S3.Transfer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PANiXiDA.Core.Application.Storage;
using PANiXiDA.Core.Infrastructure.Storage.S3.Configuration;
using PANiXiDA.Core.Infrastructure.Storage.S3.DependencyInjection;

namespace PANiXiDA.Core.Infrastructure.Storage.S3.UnitTests.DependencyInjection;

public sealed class ServiceCollectionExtensionsTests
{
    [Fact(DisplayName = "Registration binds settings and creates scoped storage with shared SDK services")]
    public async Task RegistersStorage()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AWS:ServiceURL"] = "http://localhost:8333",
            ["AWS:AuthenticationRegion"] = "us-east-1",
            ["AWS:ForcePathStyle"] = "true",
            ["S3Storage:BucketName"] = "files",
            ["S3Storage:KeyPrefix"] = "development",
            ["S3Storage:AccessKey"] = "test-access",
            ["S3Storage:SecretKey"] = "test-secret",
            ["S3Storage:MaxInMemoryDownloadParts"] = "2",
            ["S3Storage:DownloadPartSizeBytes"] = "4194304",
            ["S3Storage:PresignedUrlLifetime"] = "00:05:00"
        }).Build();
        var services = new ServiceCollection();

        services.AddS3FileStorage(configuration).ShouldBeSameAs(services);
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var firstScope = provider.CreateScope();
        using var secondScope = provider.CreateScope();
        var storage = firstScope.ServiceProvider.GetRequiredService<IFileStorage>();

        storage.ShouldBeOfType<S3FileStorage>();
        firstScope.ServiceProvider.GetRequiredService<IFileStorage>().ShouldBeSameAs(storage);
        secondScope.ServiceProvider.GetRequiredService<IFileStorage>().ShouldNotBeSameAs(storage);
        provider.GetRequiredService<ITransferUtility>().S3Client.ShouldBeSameAs(provider.GetRequiredService<IAmazonS3>());
        var options = provider.GetRequiredService<IOptions<S3StorageOptions>>().Value;
        options.KeyPrefix.ShouldBe("development");
        options.MaxInMemoryDownloadParts.ShouldBe(2);
        options.DownloadPartSizeBytes.ShouldBe(4 * 1024 * 1024);
        options.PresignedUrlLifetime.ShouldBe(TimeSpan.FromMinutes(5));
        var awsOptions = provider.GetRequiredService<AWSOptions>();
        var credentials = await awsOptions.Credentials.GetCredentialsAsync();
        credentials.AccessKey.ShouldBe("test-access");
        credentials.SecretKey.ShouldBe("test-secret");
        var clientConfig = (AmazonS3Config)provider.GetRequiredService<IAmazonS3>().Config;
        clientConfig.ServiceURL.ShouldBe("http://localhost:8333/");
        clientConfig.ForcePathStyle.ShouldBeTrue();
        clientConfig.AuthenticationRegion.ShouldBe("us-east-1");
    }

    [Fact(DisplayName = "Omitted credentials preserve the SDK credential chain and a registered clock")]
    public async Task PreservesDefaults()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["S3Storage:BucketName"] = "files"
        }).Build();
        var clock = new TestTimeProvider(DateTimeOffset.UnixEpoch);
        var services = new ServiceCollection().AddSingleton<TimeProvider>(clock).AddS3FileStorage(configuration);
        await using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<AWSOptions>().Credentials.ShouldBeNull();
        provider.GetRequiredService<TimeProvider>().ShouldBeSameAs(clock);
    }

    [Theory(DisplayName = "Presigned URLs use SDK time despite an application's shifted clock")]
    [InlineData(-10, 15)]
    [InlineData(10, 15)]
    [InlineData(-10, 7 * 24 * 60)]
    [InlineData(10, 7 * 24 * 60)]
    public async Task SigningIgnoresApplicationClock(int offsetDays, int lifetimeMinutes)
    {
        var lifetime = TimeSpan.FromMinutes(lifetimeMinutes);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AWS:ServiceURL"] = "https://storage.example",
            ["AWS:AuthenticationRegion"] = "eu-central-1",
            ["AWS:ForcePathStyle"] = "true",
            ["S3Storage:BucketName"] = "files",
            ["S3Storage:AccessKey"] = "test-access",
            ["S3Storage:SecretKey"] = "test-secret",
            ["S3Storage:PresignedUrlLifetime"] = lifetime.ToString("c", CultureInfo.InvariantCulture)
        }).Build();
        var clock = new TestTimeProvider(DateTimeOffset.UtcNow.AddDays(offsetDays));
        await using var provider = new ServiceCollection()
            .AddSingleton<TimeProvider>(clock)
            .AddS3FileStorage(configuration)
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var scope = provider.CreateScope();
        var storage = scope.ServiceProvider.GetRequiredService<IFileStorage>();
        var before = DateTimeOffset.UtcNow;

        var upload = await storage.GetPresignedUploadUrlAsync("file.txt", "text/plain", 7, TestContext.Current.CancellationToken);
        var download = await storage.GetPresignedDownloadUrlAsync("file.txt", "file.txt", "text/plain", TestContext.Current.CancellationToken);

        var after = DateTimeOffset.UtcNow;
        upload.ExpiresAt.ShouldBeInRange(before.Add(lifetime), after.Add(lifetime));
        download.ExpiresAt.ShouldBeInRange(before.Add(lifetime), after.Add(lifetime));
        foreach (var url in new[] { upload.Url, download.Url })
        {
            var parameters = HttpUtility.ParseQueryString(new Uri(url).Query);
            parameters["X-Amz-Algorithm"].ShouldBe("AWS4-HMAC-SHA256");
            var expiresIn = int.Parse(parameters["X-Amz-Expires"]!, CultureInfo.InvariantCulture);
            expiresIn.ShouldBeInRange((int)lifetime.TotalSeconds - 5, (int)lifetime.TotalSeconds);
        }

        provider.GetRequiredService<TimeProvider>().ShouldBeSameAs(clock);
    }

    [Fact(DisplayName = "Missing configuration fails at registration")]
    public void RequiresConfiguration()
    {
        var configuration = new ConfigurationBuilder().Build();

        Should.Throw<InvalidOperationException>(() => new ServiceCollection().AddS3FileStorage(configuration));
    }

    [Fact(DisplayName = "Invalid storage settings fail before constructing a storage client")]
    public async Task ValidatesOptions()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["S3Storage:BucketName"] = " "
        }).Build();
        await using var provider = new ServiceCollection().AddS3FileStorage(configuration).BuildServiceProvider();

        Should.Throw<OptionsValidationException>(() => provider.GetRequiredService<AWSOptions>());
    }

    private sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
