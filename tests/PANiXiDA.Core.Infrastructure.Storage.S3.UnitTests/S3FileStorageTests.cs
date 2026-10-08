using System.Net;
using System.Net.Http.Headers;
using Amazon.Runtime.Internal.Util;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;
using Microsoft.Extensions.Options;
using NSubstitute;
using PANiXiDA.Core.Infrastructure.Storage.S3.Configuration;

namespace PANiXiDA.Core.Infrastructure.Storage.S3.UnitTests;

public sealed class S3FileStorageTests
{
    private static readonly DateTimeOffset CurrentTime = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
    private readonly IAmazonS3 _client = Substitute.For<IAmazonS3>();
    private readonly ITransferUtility _transfer = Substitute.For<ITransferUtility>();

    [Theory(DisplayName = "Upload preserves the caller's stream position and ownership")]
    [InlineData("", "folder/File.txt")]
    [InlineData("development", "development/folder/File.txt")]
    public async Task Upload(string prefix, string expectedKey)
    {
        var storage = CreateStorage(prefix);
        using var content = new MemoryStream("prefix-content"u8.ToArray());
        content.Position = 7;
        _transfer.UploadWithResponseAsync(Arg.Any<TransferUtilityUploadRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                call.Arg<TransferUtilityUploadRequest>().InputStream.Dispose();
                return new TransferUtilityUploadResponse();
            });

        await storage.UploadAsync("folder/File.txt", content, "text/plain", TestContext.Current.CancellationToken);

        await _transfer.Received(1).UploadWithResponseAsync(Arg.Is<TransferUtilityUploadRequest>(request =>
            request.BucketName == "files" && request.Key == expectedKey &&
            ((NonDisposingWrapperStream)request.InputStream).BaseStream == content && request.ContentType == "text/plain" &&
            !request.AutoCloseStream && !request.AutoResetStreamPosition), TestContext.Current.CancellationToken);
        content.CanRead.ShouldBeTrue();
        content.Position.ShouldBe(7);
    }

    [Fact(DisplayName = "Download returns the SDK response stream without buffering the file")]
    public async Task Download()
    {
        var storage = CreateStorage();
        await using var content = new MemoryStream("content"u8.ToArray());
        _client.GetObjectAsync(Arg.Any<GetObjectRequest>(), Arg.Any<CancellationToken>())
            .Returns(new GetObjectResponse { ResponseStream = content });

        var result = await storage.DownloadAsync("file.txt", TestContext.Current.CancellationToken);

        result.ShouldBeSameAs(content);
        await _client.Received(1).GetObjectAsync(Arg.Is<GetObjectRequest>(request =>
            request.BucketName == "files" && request.Key == "development/file.txt"),
            TestContext.Current.CancellationToken);
    }

    [Theory(DisplayName = "Missing objects become FileNotFoundException while storage failures propagate")]
    [InlineData(HttpStatusCode.NotFound, "NoSuchKey", true)]
    [InlineData(HttpStatusCode.NotFound, null, true)]
    [InlineData(HttpStatusCode.NotFound, "NoSuchBucket", false)]
    [InlineData(HttpStatusCode.Forbidden, "AccessDenied", false)]
    [InlineData(HttpStatusCode.InternalServerError, "InternalError", false)]
    public async Task DownloadFailure(HttpStatusCode status, string? code, bool missingFile)
    {
        var storage = CreateStorage();
        var failure = new AmazonS3Exception("Storage failure") { StatusCode = status, ErrorCode = code };
        _client.GetObjectAsync(Arg.Any<GetObjectRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<GetObjectResponse>(failure));

        Task Action() => storage.DownloadAsync("file.txt", TestContext.Current.CancellationToken);

        if (missingFile)
        {
            var exception = await Should.ThrowAsync<FileNotFoundException>(Action);
            exception.FileName.ShouldBe("file.txt");
            exception.InnerException.ShouldBeSameAs(failure);
        }
        else
        {
            (await Should.ThrowAsync<AmazonS3Exception>(Action)).ShouldBeSameAs(failure);
        }
    }

    [Fact(DisplayName = "Delete uses the configured bucket and prefix")]
    public async Task Delete()
    {
        var storage = CreateStorage();

        await storage.DeleteAsync("file.txt", TestContext.Current.CancellationToken);

        await _client.Received(1).DeleteObjectAsync(Arg.Is<DeleteObjectRequest>(request =>
            request.BucketName == "files" && request.Key == "development/file.txt"), TestContext.Current.CancellationToken);
    }

    [Theory(DisplayName = "Upload and delete propagate storage errors")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WriteFailure(bool upload)
    {
        var storage = CreateStorage();
        var failure = new AmazonS3Exception("Access denied") { StatusCode = HttpStatusCode.Forbidden };
        _client.DeleteObjectAsync(Arg.Any<DeleteObjectRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<DeleteObjectResponse>(failure));
        _transfer.UploadWithResponseAsync(Arg.Any<TransferUtilityUploadRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<TransferUtilityUploadResponse>(failure));
        using var content = new MemoryStream();

        Task Action() => upload
            ? storage.UploadAsync("file.txt", content, "text/plain", TestContext.Current.CancellationToken)
            : storage.DeleteAsync("file.txt", TestContext.Current.CancellationToken);

        (await Should.ThrowAsync<AmazonS3Exception>(Action)).ShouldBeSameAs(failure);
    }

    [Theory(DisplayName = "Signed upload binds key, content type, size, lifetime, and endpoint protocol")]
    [InlineData("http://localhost:8333", false, Protocol.HTTP, 0)]
    [InlineData("https://storage.example", true, Protocol.HTTPS, 42)]
    [InlineData(null, false, Protocol.HTTPS, 42)]
    [InlineData(null, true, Protocol.HTTP, 42)]
    public async Task PresignedUpload(string? endpoint, bool useHttp, Protocol protocol, long size)
    {
        var storage = CreateStorage(config: new AmazonS3Config { ServiceURL = endpoint, UseHttp = useHttp });
        GetPreSignedUrlRequest? captured = null;
        _client.GetPreSignedURLAsync(Arg.Do<GetPreSignedUrlRequest>(request => captured = request)).Returns("https://signed.example/upload");

        var result = await storage.GetPresignedUploadUrlAsync("file.txt", "text/plain", size, TestContext.Current.CancellationToken);

        captured.ShouldNotBeNull();
        captured.BucketName.ShouldBe("files");
        captured.Key.ShouldBe("development/file.txt");
        captured.Verb.ShouldBe(HttpVerb.PUT);
        captured.ContentType.ShouldBe("text/plain");
        captured.Headers.ContentLength.ShouldBe(size);
        captured.Protocol.ShouldBe(protocol);
        captured.Expires.ShouldBe(CurrentTime.AddMinutes(15).UtcDateTime);
        result.Url.ShouldBe("https://signed.example/upload");
        result.ExpiresAt.ShouldBe(CurrentTime.AddMinutes(15));
        result.RequiredHeaders.ShouldHaveSingleItem().ShouldBe(new KeyValuePair<string, string>("Content-Type", "text/plain"));
    }

    [Fact(DisplayName = "Signed download encodes the suggested name and overrides response metadata")]
    public async Task PresignedDownload()
    {
        var storage = CreateStorage();
        GetPreSignedUrlRequest? captured = null;
        _client.GetPreSignedURLAsync(Arg.Do<GetPreSignedUrlRequest>(request => captured = request)).Returns("https://signed.example/download");
        const string fileName = "отчёт \"финальный\".txt";

        var result = await storage.GetPresignedDownloadUrlAsync("file.txt", fileName, "text/plain", TestContext.Current.CancellationToken);

        captured.ShouldNotBeNull();
        captured.BucketName.ShouldBe("files");
        captured.Key.ShouldBe("development/file.txt");
        captured.Verb.ShouldBe(HttpVerb.GET);
        captured.ResponseHeaderOverrides.ContentType.ShouldBe("text/plain");
        var disposition = ContentDispositionHeaderValue.Parse(captured.ResponseHeaderOverrides.ContentDisposition);
        disposition.DispositionType.ShouldBe("attachment");
        disposition.FileNameStar.ShouldBe(fileName);
        captured.Expires.ShouldBe(CurrentTime.AddMinutes(15).UtcDateTime);
        result.Url.ShouldBe("https://signed.example/download");
        result.ExpiresAt.ShouldBe(CurrentTime.AddMinutes(15));
    }

    [Theory(DisplayName = "All operations reject invalid or escaping object keys")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("/file.txt")]
    [InlineData("folder\\file.txt")]
    [InlineData("folder/./file.txt")]
    [InlineData("folder/../file.txt")]
    public async Task InvalidKeys(string? key)
    {
        var storage = CreateStorage();
        using var content = new MemoryStream();

        await Should.ThrowAsync<ArgumentException>(() => storage.UploadAsync(key!, content, "text/plain", CancellationToken.None));
        await Should.ThrowAsync<ArgumentException>(() => storage.DownloadAsync(key!, CancellationToken.None));
        await Should.ThrowAsync<ArgumentException>(() => storage.DeleteAsync(key!, CancellationToken.None));
        await Should.ThrowAsync<ArgumentException>(() => storage.GetPresignedUploadUrlAsync(key!, "text/plain", 0, CancellationToken.None));
        await Should.ThrowAsync<ArgumentException>(() => storage.GetPresignedDownloadUrlAsync(key!, "file.txt", "text/plain", CancellationToken.None));
    }

    [Theory(DisplayName = "Upload and signed URLs reject invalid content types")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("invalid")]
    [InlineData("text/plain\r\nInjected: value")]
    public async Task InvalidContentType(string? contentType)
    {
        var storage = CreateStorage();
        using var content = new MemoryStream();

        await Should.ThrowAsync<ArgumentException>(() => storage.UploadAsync("file", content, contentType!, CancellationToken.None));
        await Should.ThrowAsync<ArgumentException>(() => storage.GetPresignedUploadUrlAsync("file", contentType!, 0, CancellationToken.None));
        await Should.ThrowAsync<ArgumentException>(() => storage.GetPresignedDownloadUrlAsync("file", "file.txt", contentType!, CancellationToken.None));
    }

    [Fact(DisplayName = "Upload requires a readable stream")]
    public async Task InvalidStream()
    {
        var storage = CreateStorage();
        var content = new MemoryStream();
        await content.DisposeAsync();

        await Should.ThrowAsync<ArgumentException>(() => storage.UploadAsync("file", null!, "text/plain", CancellationToken.None));
        await Should.ThrowAsync<ArgumentException>(() => storage.UploadAsync("file", content, "text/plain", CancellationToken.None));
    }

    [Fact(DisplayName = "Signed upload rejects negative content length")]
    public async Task InvalidSize()
    {
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() =>
            CreateStorage().GetPresignedUploadUrlAsync("file", "text/plain", -1, CancellationToken.None));
    }

    [Theory(DisplayName = "Signed download requires a file name")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task InvalidFileName(string? name)
    {
        await Should.ThrowAsync<ArgumentException>(() =>
            CreateStorage().GetPresignedDownloadUrlAsync("file", name!, "text/plain", CancellationToken.None));
    }

    [Fact(DisplayName = "Already canceled operations never contact storage")]
    public async Task Cancellation()
    {
        var storage = CreateStorage();
        using var content = new MemoryStream();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => storage.UploadAsync("file", content, "text/plain", cancellation.Token));
        await Should.ThrowAsync<OperationCanceledException>(() => storage.DownloadAsync("file", cancellation.Token));
        await Should.ThrowAsync<OperationCanceledException>(() => storage.DeleteAsync("file", cancellation.Token));
        await Should.ThrowAsync<OperationCanceledException>(() => storage.GetPresignedUploadUrlAsync("file", "text/plain", 0, cancellation.Token));
        await Should.ThrowAsync<OperationCanceledException>(() => storage.GetPresignedDownloadUrlAsync("file", "file.txt", "text/plain", cancellation.Token));
        _client.ReceivedCalls().ShouldNotContain(call => call.GetMethodInfo().Name.EndsWith("Async", StringComparison.Ordinal));
        _transfer.ReceivedCalls().ShouldNotContain(call => call.GetMethodInfo().Name.EndsWith("Async", StringComparison.Ordinal));
    }

    [Theory(DisplayName = "Cancellation interrupts waiting for URL signing")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SigningCancellation(bool upload)
    {
        var storage = CreateStorage();
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _client.GetPreSignedURLAsync(Arg.Any<GetPreSignedUrlRequest>()).Returns(completion.Task);
        using var cancellation = new CancellationTokenSource();
        var operation = upload
            ? storage.GetPresignedUploadUrlAsync("file", "text/plain", 0, cancellation.Token) as Task
            : storage.GetPresignedDownloadUrlAsync("file", "file.txt", "text/plain", cancellation.Token);

        await cancellation.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => operation);
        completion.SetResult("https://signed.example");
    }

    private S3FileStorage CreateStorage(string prefix = "development", AmazonS3Config? config = null)
    {
        _client.Config.Returns(config ?? new AmazonS3Config());
        _transfer.S3Client.Returns(_client);
        return new S3FileStorage(_transfer, Options.Create(new S3StorageOptions
        {
            BucketName = "files",
            KeyPrefix = prefix
        }), new FixedTimeProvider());
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => CurrentTime;
    }
}
