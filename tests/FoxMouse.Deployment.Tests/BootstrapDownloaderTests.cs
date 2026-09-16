using FoxMouse.Bootstrap;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;

namespace FoxMouse.Deployment.Tests;

public sealed class BootstrapDownloaderTests
{
    [Fact]
    public async Task SuccessfulDownloadExposesTotalLengthAndReceivedBytes()
    {
        using Handler handler = new(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(Bytes) });
        using BootstrapDownloader downloader = new(handler);
        using MemoryStream destination = new();
        long received = 0;
        await downloader.DownloadAsync(Source, Hash, 100, destination,
            new CallbackProgress(value => received = value), default);
        Assert.Equal(Bytes.Length, downloader.ExpectedLength);
        Assert.Equal(Bytes.Length, received);
    }

    private static readonly Uri Source = new("https://download.microsoft.com/runtime.exe");
    private static readonly byte[] Bytes = [1, 2, 3, 4];
    private static string Hash => Convert.ToHexString(SHA512.HashData(Bytes));
    private sealed class IgnoreProgress : IProgress<long> { public void Report(long value) { } }
    private sealed class CallbackProgress(Action<long> callback) : IProgress<long>
    { public void Report(long value) => callback(value); }

    [Fact]
    public async Task CancelledBeforeRequestDoesNotContactServer()
    {
        using Handler handler = new(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(Bytes) });
        using BootstrapDownloader downloader = new(handler);
        using MemoryStream destination = new();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => downloader.DownloadAsync(
            Source, Hash, 100, destination, new IgnoreProgress(), cancellation.Token));
        Assert.Equal(0, handler.Requests);
        Assert.Equal(0, destination.Length);
    }

    [Fact]
    public async Task CancellationAfterFirstChunkErasesPartialDownload()
    {
        using Handler handler = new(_ => new(HttpStatusCode.OK) { Content = new StreamContent(new ChunkedStream(false)) });
        using BootstrapDownloader downloader = new(handler);
        using MemoryStream destination = new();
        using CancellationTokenSource cancellation = new();
        int progressCalls = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => downloader.DownloadAsync(
            Source, Hash, 100, destination, new CallbackProgress(_ => { progressCalls++; cancellation.Cancel(); }), cancellation.Token));
        Assert.Equal(1, progressCalls);
        Assert.Equal(0, destination.Length);
        Assert.Equal(0, destination.Position);
    }

    [Fact]
    public async Task DisconnectionAfterFirstChunkErasesPartialDownload()
    {
        using Handler handler = new(_ => new(HttpStatusCode.OK) { Content = new StreamContent(new ChunkedStream(true)) });
        using BootstrapDownloader downloader = new(handler);
        using MemoryStream destination = new();
        long received = 0;
        await Assert.ThrowsAsync<IOException>(() => downloader.DownloadAsync(
            Source, Hash, 100, destination, new CallbackProgress(value => received = value), default));
        Assert.Equal(2, received);
        Assert.Equal(0, destination.Length);
    }

    [Fact]
    public async Task ChunkedResponseWithoutLengthCannotExceedLimit()
    {
        using Handler handler = new(_ => new(HttpStatusCode.OK) { Content = new StreamContent(new ChunkedStream(false)) });
        using BootstrapDownloader downloader = new(handler);
        using MemoryStream destination = new();
        await Assert.ThrowsAsync<InvalidDataException>(() => downloader.DownloadAsync(
            Source, Hash, 3, destination, new IgnoreProgress(), default));
        Assert.Equal(0, destination.Length);
    }

    [Fact]
    public async Task PrematureEndRejectsEvenAnOtherwiseCorrectChecksum()
    {
        using Handler handler = new(_ =>
        {
            StreamContent content = new(new ChunkedStream(false));
            content.Headers.ContentLength = 8;
            return new(HttpStatusCode.OK) { Content = content };
        });
        using BootstrapDownloader downloader = new(handler);
        using MemoryStream destination = new();
        await Assert.ThrowsAsync<InvalidDataException>(() => downloader.DownloadAsync(
            Source, Hash, 100, destination, new IgnoreProgress(), default));
        Assert.Equal(0, destination.Length);
    }

    private sealed class ChunkedStream(bool disconnect) : MemoryStream(Bytes)
    {
        public override bool CanSeek => false;
        private int reads;
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (disconnect && reads++ > 0) throw new IOException("Simulated disconnected dependency server");
            return base.ReadAsync(buffer, offset, Math.Min(count, 2), cancellationToken);
        }
    }

    [Fact]
    public async Task VerifiedDownloadReturnsTheCompleteBytes()
    {
        using Handler handler = new(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(Bytes) });
        using BootstrapDownloader downloader = new(handler);
        using MemoryStream destination = new();
        await downloader.DownloadAsync(Source, Hash, 100, destination, new IgnoreProgress(), default);
        Assert.Equal(Bytes, destination.ToArray());
        Assert.Equal(0, destination.Position);
    }

    [Theory]
    [InlineData("https://attacker.test/payload.exe")]
    [InlineData("http://download.microsoft.com/payload.exe")]
    public async Task UnsafeRedirectIsRejectedBeforeSendingAnotherRequest(string target)
    {
        using Handler handler = new(_ =>
        {
            HttpResponseMessage response = new(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri(target);
            return response;
        });
        using BootstrapDownloader downloader = new(handler);
        using MemoryStream destination = new();
        await Assert.ThrowsAsync<InvalidDataException>(() => downloader.DownloadAsync(Source, Hash, 100, destination, new IgnoreProgress(), default));
        Assert.Equal(1, handler.Requests);
        Assert.Equal(0, destination.Length);
    }

    [Fact]
    public async Task RedirectLoopIsBounded()
    {
        using Handler handler = new(_ =>
        {
            HttpResponseMessage response = new(HttpStatusCode.Redirect);
            response.Headers.Location = Source;
            return response;
        });
        using BootstrapDownloader downloader = new(handler);
        using MemoryStream destination = new();
        await Assert.ThrowsAsync<InvalidDataException>(() => downloader.DownloadAsync(Source, Hash, 100, destination, new IgnoreProgress(), default));
        Assert.Equal(6, handler.Requests);
    }

    [Theory]
    [InlineData(3, false)]
    [InlineData(100, true)]
    public async Task SizeOrChecksumFailureErasesStagingBytes(int limit, bool corrupt)
    {
        using Handler handler = new(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(Bytes) });
        using BootstrapDownloader downloader = new(handler);
        using MemoryStream destination = new();
        await Assert.ThrowsAsync<InvalidDataException>(() => downloader.DownloadAsync(Source, corrupt ? new string('0', 128) : Hash, limit, destination, new IgnoreProgress(), default));
        Assert.Equal(0, destination.Length);
    }

    [Fact]
    public async Task HttpFailureCannotReturnAPayload()
    {
        using Handler handler = new(_ => new(HttpStatusCode.ServiceUnavailable));
        using BootstrapDownloader downloader = new(handler);
        using MemoryStream destination = new();
        await Assert.ThrowsAsync<HttpRequestException>(() => downloader.DownloadAsync(Source, Hash, 100, destination, new IgnoreProgress(), default));
        Assert.Empty(destination.ToArray());
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests++;
            return Task.FromResult(respond(request));
        }
    }
}
