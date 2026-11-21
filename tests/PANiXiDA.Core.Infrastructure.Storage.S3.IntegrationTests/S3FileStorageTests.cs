using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Amazon.Runtime;
using Amazon.Runtime.Endpoints;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.DependencyInjection;
using PANiXiDA.Core.Application.Storage;
using PANiXiDA.Core.Application.Storage.Constants;
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

    [Theory(DisplayName = "Metadata queries return current stored values without downloading file content")]
    [InlineData("folder/File.txt", "content")]
    [InlineData("папка/файл + %23.txt", "данные")]
    [InlineData("empty.txt", "")]
    public async Task Metadata(string key, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        using var content = new MemoryStream(bytes);
        await _storage.UploadAsync(key, content, "text/plain", TestContext.Current.CancellationToken);
        var requests = new ConcurrentQueue<AmazonWebServiceRequest>();
        _client.ShouldBeOfType<AmazonS3Client>().BeforeRequestEvent += (_, args) =>
        {
            if (args is WebServiceRequestEventArgs requestArgs)
            {
                requests.Enqueue(requestArgs.Request);
            }
        };

        var metadata = await _storage.GetMetadataAsync(key, TestContext.Current.CancellationToken);

        metadata.ShouldNotBeNull();
        metadata.ContentType.ShouldBe("text/plain");
        metadata.Size.ShouldBe(bytes.LongLength);
        var request = requests.ShouldHaveSingleItem().ShouldBeOfType<GetObjectMetadataRequest>();
        request.BucketName.ShouldBe(SeaweedFsContainerFixture.BucketName);
        request.Key.ShouldBe($"{_prefix}/{key}");
    }

    [Fact(DisplayName = "Metadata queries return null before upload and after deletion")]
    public async Task MissingMetadata()
    {
        using var content = new MemoryStream("content"u8.ToArray());

        var missing = await _storage.GetMetadataAsync("file.txt", TestContext.Current.CancellationToken);
        await _storage.UploadAsync("file.txt", content, "text/plain", TestContext.Current.CancellationToken);
        var uploaded = await _storage.GetMetadataAsync("file.txt", TestContext.Current.CancellationToken);
        await _storage.DeleteAsync("file.txt", TestContext.Current.CancellationToken);
        var deleted = await _storage.GetMetadataAsync("file.txt", TestContext.Current.CancellationToken);

        missing.ShouldBeNull();
        uploaded.ShouldNotBeNull();
        uploaded.Size.ShouldBe(content.Length);
        deleted.ShouldBeNull();
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
        await Should.ThrowAsync<OperationCanceledException>(() => _storage.GetMetadataAsync("existing.txt", cancellation.Token));
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
        var metadata = await _storage.GetMetadataAsync("file.bin", TestContext.Current.CancellationToken);
        metadata.ShouldNotBeNull();
        metadata.ContentType.ShouldBe("application/octet-stream");
        metadata.Size.ShouldBe(size);
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
        var endpoint = _client.Config.DetermineServiceOperationEndpoint(
            new ServiceOperationEndpointParameters(new GetPreSignedUrlRequest
            {
                BucketName = SeaweedFsContainerFixture.BucketName,
                Key = $"{_prefix}/{key}"
            }));
        var before = CorrectClockSkew.GetCorrectedUtcNowForEndpoint(endpoint.URL);
        var upload = await _storage.GetPresignedUploadUrlAsync(key, "text/plain", bytes.Length, TestContext.Current.CancellationToken);
        upload.ExpiresAt.ShouldBeInRange(
            before.AddMinutes(5), CorrectClockSkew.GetCorrectedUtcNowForEndpoint(endpoint.URL).AddMinutes(5));
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

    [Theory(DisplayName = "Versioned uploads retain metadata and serve exact versions after replacement and deletion")]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(17 * 1024 * 1024)]
    public async Task VersionedRoundTrip_Should_RetainVersions_When_CurrentContentChanges(int size)
    {
        var token = TestContext.Current.CancellationToken;
        var bytes = new byte[size];
        RandomNumberGenerator.Fill(bytes);
        using var firstContent = new MemoryStream(bytes);
        var first = await _storage.UploadAsync(
            "versioned.bin", firstContent, "application/octet-stream",
            new Dictionary<string, string> { ["operation-id"] = "first" }, token);
        first.VersionId.ShouldNotBeNullOrWhiteSpace();
        using var secondContent = new MemoryStream([4, 5, 6, 7]);
        var second = await _storage.UploadAsync(
            "versioned.bin", secondContent, "application/octet-stream",
            new Dictionary<string, string> { ["operation-id"] = "second" }, token);
        second.VersionId.ShouldNotBeNullOrWhiteSpace();
        second.VersionId.ShouldNotBe(first.VersionId);
        using var neighbor = new MemoryStream([9]);
        await _storage.UploadAsync("versioned.bin-neighbor", neighbor, "text/plain", token);

        var metadata = await _storage.GetMetadataAsync("versioned.bin", first.VersionId!, token);

        metadata.ShouldNotBeNull();
        metadata.VersionId.ShouldBe(first.VersionId);
        metadata.Size.ShouldBe(size);
        metadata.Metadata["operation-id"].ShouldBe("first");
        (await _storage.GetMetadataAsync("versioned.bin", token))!.VersionId.ShouldBe(second.VersionId);
        await using (var oldContent = await _storage.DownloadAsync("versioned.bin", first.VersionId!, token))
        {
            (await SHA256.HashDataAsync(oldContent, token)).ShouldBe(SHA256.HashData(bytes));
        }
        var versions = new List<string>();
        await foreach (var version in _storage.GetVersionsAsync("versioned.bin", token))
        {
            versions.Add(version.VersionId);
            version.LastModified.ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddMinutes(-5));
        }
        versions.Order().ShouldBe(new[] { first.VersionId, second.VersionId }.Order());
        await _storage.DeleteAsync("versioned.bin", token);
        (await _storage.GetMetadataAsync("versioned.bin", token)).ShouldBeNull();
        var historical = new List<string>();
        await foreach (var version in _storage.GetVersionsAsync("versioned.bin", token))
        {
            historical.Add(version.VersionId);
        }
        historical.Order().ShouldBe(versions.Order());
        var url = await _storage.GetPresignedDownloadUrlAsync(
            "versioned.bin", first.VersionId!, "original.bin", "application/octet-stream", token);
        using var client = new HttpClient();
        using var downloaded = await client.GetAsync(url.Url, token);
        downloaded.EnsureSuccessStatusCode();
        SHA256.HashData(await downloaded.Content.ReadAsByteArrayAsync(token)).ShouldBe(SHA256.HashData(bytes));
        firstContent.CanRead.ShouldBeTrue();
        secondContent.CanRead.ShouldBeTrue();
    }

    [Fact(DisplayName = "Identical bytes produce independent versions with independent operation metadata")]
    public async Task UploadAsync_Should_IdentifyEachAttempt_When_BytesAreIdentical()
    {
        var token = TestContext.Current.CancellationToken;
        using var content = new MemoryStream([1, 2, 3]);
        var first = await _storage.UploadAsync(
            "identical.bin", content, "text/plain",
            new Dictionary<string, string> { ["operation-id"] = "first" }, token);
        content.Position = 0;

        var second = await _storage.UploadAsync(
            "identical.bin", content, "text/plain",
            new Dictionary<string, string> { ["operation-id"] = "second" }, token);

        first.VersionId.ShouldNotBeNullOrWhiteSpace();
        second.VersionId.ShouldNotBeNullOrWhiteSpace();
        second.VersionId.ShouldNotBe(first.VersionId);
        var earlier = await _storage.GetMetadataAsync("identical.bin", first.VersionId!, token);
        earlier.ShouldNotBeNull();
        earlier.Metadata["operation-id"].ShouldBe("first");
        var current = await _storage.GetMetadataAsync("identical.bin", second.VersionId!, token);
        current.ShouldNotBeNull();
        current.Metadata["operation-id"].ShouldBe("second");
    }

    [Fact(DisplayName = "Missing versions do not fall back to existing current content")]
    public async Task VersionReads_Should_ReportMissing_When_RequestedVersionWasPermanentlyDeleted()
    {
        var token = TestContext.Current.CancellationToken;
        using var firstContent = new MemoryStream("first"u8.ToArray());
        var first = await _storage.UploadAsync(
            "missing-version.txt", firstContent, "text/plain", new Dictionary<string, string>(), token);
        using var secondContent = new MemoryStream("second"u8.ToArray());
        var second = await _storage.UploadAsync(
            "missing-version.txt", secondContent, "text/plain", new Dictionary<string, string>(), token);
        first.VersionId.ShouldNotBeNullOrWhiteSpace();
        await _client.DeleteObjectAsync(new DeleteObjectRequest
        {
            BucketName = SeaweedFsContainerFixture.BucketName,
            Key = $"{_prefix}/missing-version.txt",
            VersionId = first.VersionId
        }, token);

        var missing = await _storage.GetMetadataAsync("missing-version.txt", first.VersionId!, token);

        missing.ShouldBeNull();
        await Should.ThrowAsync<FileNotFoundException>(() =>
            _storage.DownloadAsync("missing-version.txt", first.VersionId!, token));
        (await ReadTextAsync(_storage, "missing-version.txt")).ShouldBe("second");
        (await _storage.GetMetadataAsync("missing-version.txt", token))!.VersionId.ShouldBe(second.VersionId);
        await foreach (var version in _storage.GetVersionsAsync("absent", token))
        {
            Assert.Fail($"An absent key returned version {version.VersionId}.");
        }
    }

    [Theory(DisplayName = "Copy preserves the requested source version and deletion removes only its exact version")]
    [InlineData("source.txt", "old content")]
    [InlineData("папка/файл + %23.txt", "данные")]
    [InlineData("empty.txt", "")]
    public async Task CopyAsync_Should_PreserveVersions_When_ContentIsRelocated(string sourceKey, string text)
    {
        var token = TestContext.Current.CancellationToken;
        var bytes = Encoding.UTF8.GetBytes(text);
        using var original = new MemoryStream(bytes);
        var source = await _storage.UploadAsync(sourceKey, original, "text/plain",
            new Dictionary<string, string> { ["source-only"] = "original" }, token);
        using var replacement = new MemoryStream("latest"u8.ToArray());
        var current = await _storage.UploadAsync(sourceKey, replacement, "application/octet-stream",
            new Dictionary<string, string>(), token);
        const string destinationKey = "destination/файл + %.txt";
        using var earlierDestination = new MemoryStream("previous"u8.ToArray());
        var previous = await _storage.UploadAsync(destinationKey, earlierDestination, "text/plain",
            new Dictionary<string, string>(), token);
        var requests = new ConcurrentQueue<AmazonWebServiceRequest>();
        _client.ShouldBeOfType<AmazonS3Client>().BeforeRequestEvent += (_, args) =>
        {
            if (args is WebServiceRequestEventArgs requestArgs)
            {
                requests.Enqueue(requestArgs.Request);
            }
        };

        var copied = await _storage.CopyAsync(
            sourceKey, source.VersionId!, destinationKey, "text/plain", bytes.LongLength,
            new Dictionary<string, string> { [FileMetadataNames.RelocationAttemptId] = "attempt-1" }, token);

        copied.VersionId.ShouldNotBeNullOrWhiteSpace();
        copied.VersionId.ShouldNotBe(source.VersionId);
        copied.VersionId.ShouldNotBe(previous.VersionId);
        var request = requests.ShouldHaveSingleItem().ShouldBeOfType<CopyObjectRequest>();
        request.SourceKey.ShouldBe($"{_prefix}/{sourceKey}");
        request.SourceVersionId.ShouldBe(source.VersionId);
        request.DestinationKey.ShouldBe($"{_prefix}/{destinationKey}");
        var metadata = await _storage.GetMetadataAsync(destinationKey, copied.VersionId!, token);
        metadata.ShouldNotBeNull();
        metadata.VersionId.ShouldBe(copied.VersionId);
        metadata.ContentType.ShouldBe("text/plain");
        metadata.Size.ShouldBe(bytes.LongLength);
        metadata.Metadata.Count.ShouldBe(1);
        metadata.Metadata[FileMetadataNames.RelocationAttemptId].ShouldBe("attempt-1");
        await using (var downloaded = await _storage.DownloadAsync(destinationKey, copied.VersionId!, token))
        {
            (await SHA256.HashDataAsync(downloaded, token)).ShouldBe(SHA256.HashData(bytes));
        }
        (await _storage.GetMetadataAsync(sourceKey, source.VersionId!, token)).ShouldNotBeNull();
        (await _storage.GetMetadataAsync(sourceKey, token))!.VersionId.ShouldBe(current.VersionId);
        await _storage.DeleteAsync(sourceKey, source.VersionId!, token);
        await _storage.DeleteAsync(sourceKey, source.VersionId!, token);
        (await _storage.GetMetadataAsync(sourceKey, source.VersionId!, token)).ShouldBeNull();
        (await _storage.GetMetadataAsync(sourceKey, token))!.VersionId.ShouldBe(current.VersionId);
        (await ReadTextAsync(_storage, sourceKey)).ShouldBe("latest");
        (await _storage.GetMetadataAsync(destinationKey, copied.VersionId!, token)).ShouldNotBeNull();
        await _storage.DeleteAsync(destinationKey, copied.VersionId!, token);
        (await _storage.GetMetadataAsync(destinationKey, copied.VersionId!, token)).ShouldBeNull();
        (await _storage.GetMetadataAsync(destinationKey, previous.VersionId!, token)).ShouldNotBeNull();
        (await ReadTextAsync(_storage, destinationKey)).ShouldBe("previous");
    }

    [Fact(DisplayName = "Copying a missing source version never falls back to current source content")]
    public async Task CopyAsync_Should_ReportMissingVersion_When_SourceVersionWasDeleted()
    {
        var token = TestContext.Current.CancellationToken;
        using var original = new MemoryStream("original"u8.ToArray());
        var source = await _storage.UploadAsync("source.txt", original, "text/plain",
            new Dictionary<string, string>(), token);
        using var replacement = new MemoryStream("current"u8.ToArray());
        await _storage.UploadAsync("source.txt", replacement, "text/plain", token);
        await _storage.DeleteAsync("source.txt", source.VersionId!, token);

        await Should.ThrowAsync<AmazonS3Exception>(() => _storage.CopyAsync(
            "source.txt", source.VersionId!, "destination.txt", "text/plain", original.Length,
            new Dictionary<string, string>(), token));

        (await _storage.GetMetadataAsync("destination.txt", token)).ShouldBeNull();
        (await ReadTextAsync(_storage, "source.txt")).ShouldBe("current");
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
