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
        provider.GetRequiredService<TimeProvider>().ShouldBeSameAs(TimeProvider.System);
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
        var clock = new TestTimeProvider();
        var services = new ServiceCollection().AddSingleton<TimeProvider>(clock).AddS3FileStorage(configuration);
        await using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<AWSOptions>().Credentials.ShouldBeNull();
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

    private sealed class TestTimeProvider : TimeProvider;
}
