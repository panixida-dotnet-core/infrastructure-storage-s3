using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.DependencyInjection;
using PANiXiDA.Core.Application.Storage;
using PANiXiDA.Core.Infrastructure.Storage.S3.IntegrationTests.Infrastructure;

namespace PANiXiDA.Core.Infrastructure.Storage.S3.IntegrationTests;

public sealed class S3FileStorageTests : IClassFixture<SeaweedFsContainerFixture>, IAsyncDisposable
{
    private readonly SeaweedFsContainerFixture _fixture;
    private readonly string _prefix = $"tests/{Guid.NewGuid():N}";
    private readonly ServiceProvider _services;
    private readonly IServiceScope _scope;
    private readonly IFileStorage _storage;
    private readonly IAmazonS3 _client;

    public S3FileStorageTests(SeaweedFsContainerFixture fixture)
    {
        _fixture = fixture;
        _services = fixture.CreateServices(_prefix);
        _scope = _services.CreateScope();
        _storage = _scope.ServiceProvider.GetRequiredService<IFileStorage>();
        _client = _services.GetRequiredService<IAmazonS3>();
    }

    [Theory(DisplayName = "Upload, download, and delete preserve content and object keys")]
    [InlineData("folder/File.txt", "content")]
    [InlineData("папка/файл + %23.txt", "данные")]
    [InlineData("empty.txt", "")]
    public async Task RoundTrip(string key, string text)
    {
        using var content = new MemoryStream(Encoding.UTF8.GetBytes(text));

        await _storage.UploadAsync(key, content, "text/plain", TestContext.Current.CancellationToken);

        content.CanRead.ShouldBeTrue();
        (await ReadTextAsync(_storage, key)).ShouldBe(text);
        var metadata = await _client.GetObjectMetadataAsync(SeaweedFsContainerFixture.BucketName,
            $"{_prefix}/{key}", TestContext.Current.CancellationToken);
        metadata.Headers.ContentType.ShouldBe("text/plain");
        metadata.Headers.ContentLength.ShouldBe(content.Length);

        await _storage.DeleteAsync(key, TestContext.Current.CancellationToken);
        await _storage.DeleteAsync(key, TestContext.Current.CancellationToken);

        await Should.ThrowAsync<FileNotFoundException>(() => _storage.DownloadAsync(key, TestContext.Current.CancellationToken));
    }

    [Fact(DisplayName = "The SDK rejects already canceled transfers and deletion without changing stored objects")]
    public async Task AlreadyCanceledOperations()
    {
        using var original = new MemoryStream("original"u8.ToArray());
        await _storage.UploadAsync("existing.txt", original, "text/plain", TestContext.Current.CancellationToken);
        using var content = new MemoryStream("content"u8.ToArray());
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => _storage.UploadAsync("canceled.txt", content, "text/plain", cancellation.Token));
        await Should.ThrowAsync<OperationCanceledException>(() => _storage.DownloadAsync("existing.txt", cancellation.Token));
        await Should.ThrowAsync<OperationCanceledException>(() => _storage.DeleteAsync("existing.txt", cancellation.Token));

        (await ReadTextAsync(_storage, "existing.txt")).ShouldBe("original");
        await Should.ThrowAsync<FileNotFoundException>(() => _storage.DownloadAsync("canceled.txt", TestContext.Current.CancellationToken));
        content.CanRead.ShouldBeTrue();
    }

    [Fact(DisplayName = "Uploading an existing key replaces content and starts at the current stream position")]
    public async Task ReplaceFromCurrentPosition()
    {
        using var first = new MemoryStream("original"u8.ToArray());
        await _storage.UploadAsync("file.txt", first, "text/plain", TestContext.Current.CancellationToken);
        using var replacement = new MemoryStream("prefix-replacement"u8.ToArray());
        replacement.Position = 7;

        await _storage.UploadAsync("file.txt", replacement, "text/plain", TestContext.Current.CancellationToken);

        (await ReadTextAsync(_storage, "file.txt")).ShouldBe("replacement");
        replacement.CanRead.ShouldBeTrue();
    }

    [Theory(DisplayName = "File size selects single PUT or multipart upload and downloaded content stays intact")]
    [InlineData(1024, false)]
    [InlineData((16 * 1024 * 1024) - 1, false)]
    [InlineData(16 * 1024 * 1024, true)]
    [InlineData((16 * 1024 * 1024) + 1, true)]
    [InlineData((20 * 1024 * 1024) + 1, true)]
    [InlineData((48 * 1024 * 1024) + 1, true)]
    public async Task UploadStrategyAndDownloadIntegrity(int size, bool multipart)
    {
        var requests = new ConcurrentQueue<AmazonWebServiceRequest>();
        _client.ShouldBeOfType<AmazonS3Client>().BeforeRequestEvent += (_, args) =>
        {
            if (args is WebServiceRequestEventArgs requestArgs)
            {
                requests.Enqueue(requestArgs.Request);
            }
        };
        var bytes = RandomNumberGenerator.GetBytes(size);
        using var content = new MemoryStream(bytes);

        await _storage.UploadAsync("file.bin", content, "application/octet-stream", TestContext.Current.CancellationToken);

        var uploads = requests.ToArray();
        if (multipart)
        {
            uploads.OfType<PutObjectRequest>().ShouldBeEmpty();
            uploads.OfType<InitiateMultipartUploadRequest>().ShouldHaveSingleItem();
            var parts = uploads.OfType<UploadPartRequest>().OrderBy(part => part.PartNumber).ToArray();
            parts.Length.ShouldBeGreaterThan(1);
            parts.Select(part => part.PartNumber).ShouldBe(Enumerable.Range(1, parts.Length).Select(number => (int?)number));
            parts.Sum(part => part.PartSize).ShouldBe(size);
            var uploadId = parts.Select(part => part.UploadId).Distinct().ShouldHaveSingleItem();
            uploadId.ShouldNotBeNullOrWhiteSpace();
            var completed = uploads.OfType<CompleteMultipartUploadRequest>().ShouldHaveSingleItem();
            completed.UploadId.ShouldBe(uploadId);
            completed.PartETags.Select(part => part.PartNumber).Order().ShouldBe(parts.Select(part => part.PartNumber));
        }
        else
        {
            uploads.OfType<PutObjectRequest>().ShouldHaveSingleItem();
            uploads.OfType<InitiateMultipartUploadRequest>().ShouldBeEmpty();
            uploads.OfType<UploadPartRequest>().ShouldBeEmpty();
            uploads.OfType<CompleteMultipartUploadRequest>().ShouldBeEmpty();
        }

        await using var downloaded = await _storage.DownloadAsync("file.bin", TestContext.Current.CancellationToken);
        var hash = await SHA256.HashDataAsync(downloaded, TestContext.Current.CancellationToken);
        hash.ShouldBe(SHA256.HashData(bytes));
        const int downloadPartSize = 8 * 1024 * 1024;
        var downloads = requests.OfType<GetObjectRequest>().OrderBy(request => request.ByteRange.Start).ToArray();
        var partCount = (size + downloadPartSize - 1) / downloadPartSize;
        downloads.Length.ShouldBe(partCount);
        downloads.Select(request => request.ByteRange.Start)
            .ShouldBe(Enumerable.Range(0, partCount).Select(part => (long)part * downloadPartSize));
        downloads.ShouldAllBe(request => request.PartNumber == null);
        var metadata = await _client.GetObjectMetadataAsync(SeaweedFsContainerFixture.BucketName,
            $"{_prefix}/file.bin", TestContext.Current.CancellationToken);
        metadata.Headers.ContentLength.ShouldBe(size);
        content.CanRead.ShouldBeTrue();
    }

    [Fact(DisplayName = "Prefixes isolate objects with the same application key")]
    public async Task PrefixIsolation()
    {
        await using var otherServices = _fixture.CreateServices($"production/{Guid.NewGuid():N}");
        using var otherScope = otherServices.CreateScope();
        var other = otherScope.ServiceProvider.GetRequiredService<IFileStorage>();
        using var first = new MemoryStream("development"u8.ToArray());
        using var second = new MemoryStream("production"u8.ToArray());
        await _storage.UploadAsync("file.txt", first, "text/plain", TestContext.Current.CancellationToken);
        await other.UploadAsync("file.txt", second, "text/plain", TestContext.Current.CancellationToken);

        (await ReadTextAsync(_storage, "file.txt")).ShouldBe("development");
        (await ReadTextAsync(other, "file.txt")).ShouldBe("production");
        await _storage.DeleteAsync("file.txt", TestContext.Current.CancellationToken);
        (await ReadTextAsync(other, "file.txt")).ShouldBe("production");
    }

    [Fact(DisplayName = "Upload accepts a non-seekable network stream and leaves it open")]
    public async Task UploadNetworkStream()
    {
        using var original = new MemoryStream("content"u8.ToArray());
        await _storage.UploadAsync("source.txt", original, "text/plain", TestContext.Current.CancellationToken);
        await using var source = await _storage.DownloadAsync("source.txt", TestContext.Current.CancellationToken);
        source.CanSeek.ShouldBeFalse();

        await _storage.UploadAsync("copy.txt", source, "text/plain", TestContext.Current.CancellationToken);

        source.CanRead.ShouldBeTrue();
        (await ReadTextAsync(_storage, "copy.txt")).ShouldBe("content");
    }

    [Theory(DisplayName = "Signed PUT and GET work without SDK credentials in the HTTP client")]
    [InlineData("folder/file.txt", "content")]
    [InlineData("папка/файл + %23.txt", "данные")]
    [InlineData("empty.txt", "")]
    public async Task PresignedRoundTrip(string key, string text)
    {
        using var http = new HttpClient();
        var bytes = Encoding.UTF8.GetBytes(text);
        var before = DateTimeOffset.UtcNow;
        var upload = await _storage.GetPresignedUploadUrlAsync(key, "text/plain", bytes.Length, TestContext.Current.CancellationToken);
        upload.ExpiresAt.ShouldBeInRange(before.AddMinutes(5), DateTimeOffset.UtcNow.AddMinutes(5));
        new Uri(upload.Url).Scheme.ShouldBe(Uri.UriSchemeHttp);
        await Should.ThrowAsync<FileNotFoundException>(() => _storage.DownloadAsync(key, TestContext.Current.CancellationToken));
        using var request = new HttpRequestMessage(HttpMethod.Put, upload.Url) { Content = new ByteArrayContent(bytes) };
        foreach (var header in upload.RequiredHeaders)
        {
            request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value).ShouldBeTrue();
        }

        using var uploaded = await http.SendAsync(request, TestContext.Current.CancellationToken);
        uploaded.EnsureSuccessStatusCode();
        const string fileName = "отчёт \"финальный\".txt";
        var download = await _storage.GetPresignedDownloadUrlAsync(key, fileName, "application/octet-stream", TestContext.Current.CancellationToken);
        using var downloaded = await http.GetAsync(download.Url, TestContext.Current.CancellationToken);

        downloaded.EnsureSuccessStatusCode();
        (await downloaded.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken)).ShouldBe(bytes);
        downloaded.Content.Headers.ContentType!.MediaType.ShouldBe("application/octet-stream");
        downloaded.Content.Headers.ContentDisposition!.DispositionType.ShouldBe("attachment");
        downloaded.Content.Headers.ContentDisposition.FileNameStar.ShouldBe(fileName);
        (await ReadTextAsync(_storage, key)).ShouldBe(text);
    }

    [Theory(DisplayName = "Signed uploads reject a changed content length or content type")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RejectChangedUploadHeaders(bool changeLength)
    {
        using var http = new HttpClient();
        var upload = await _storage.GetPresignedUploadUrlAsync("file.txt", "text/plain", 7, TestContext.Current.CancellationToken);
        using var content = new ByteArrayContent(changeLength ? "too-long"u8.ToArray() : "content"u8.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue(changeLength ? "text/plain" : "application/json");

        using var response = await http.PutAsync(upload.Url, content, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        await Should.ThrowAsync<FileNotFoundException>(() => _storage.DownloadAsync("file.txt", TestContext.Current.CancellationToken));
    }

    [Fact(DisplayName = "Signed download rejects changing the response content type")]
    public async Task RejectChangedDownloadUrl()
    {
        using var content = new MemoryStream("content"u8.ToArray());
        await _storage.UploadAsync("file.txt", content, "text/plain", TestContext.Current.CancellationToken);
        var download = await _storage.GetPresignedDownloadUrlAsync("file.txt", "file.txt", "text/plain", TestContext.Current.CancellationToken);
        using var http = new HttpClient();

        using var response = await http.GetAsync($"{download.Url}&response-content-type=application/json", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact(DisplayName = "Generating a download URL does not require an existing object")]
    public async Task PresignMissingFile()
    {
        var download = await _storage.GetPresignedDownloadUrlAsync("missing.txt", "file.txt", "text/plain", TestContext.Current.CancellationToken);
        using var http = new HttpClient();

        using var response = await http.GetAsync(download.Url, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    public async ValueTask DisposeAsync()
    {
        _scope.Dispose();
        await _services.DisposeAsync();
    }

    private static async Task<string> ReadTextAsync(IFileStorage storage, string key)
    {
        await using var stream = await storage.DownloadAsync(key, TestContext.Current.CancellationToken);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
    }
}
