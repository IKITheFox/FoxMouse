using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace FoxMouse.Bootstrap
{
    /// <summary>Downloads only pinned Microsoft dependencies. No installer execution occurs here.</summary>
    public sealed class BootstrapDownloader : IDisposable
    {
        private readonly HttpClient client;
        public long? ExpectedLength { get; private set; }

        // The download writer must already be closed. Recheck the exact file
        // under a read-only lease that prevents replacement during execution.
        public static Task<int> RunVerifiedAsync(string file, string hash,
            Func<string, Task<int>> execute, CancellationToken cancellationToken)
        {
            return Task.Run(async delegate
            {
                for (int attempt = 0; ; attempt++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        using (FileStream verified = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                        {
                            BootstrapDependencyPolicy.VerifySha512(verified, hash);
                            cancellationToken.ThrowIfCancellationRequested();
                            return await execute(file).ConfigureAwait(false);
                        }
                    }
                    catch (Exception exception)
                    {
                        var native = exception as System.ComponentModel.Win32Exception;
                        int code = native == null ? exception.HResult & 0xffff : native.NativeErrorCode;
                        if (attempt >= 2 || (code != 32 && code != 33)) throw;
                    }
                    // C# 5 (Windows inbox compiler) cannot await inside catch.
                    await Task.Delay(250 * (attempt + 1), cancellationToken).ConfigureAwait(false);
                }
            });
        }

        public BootstrapDownloader() : this(new HttpClientHandler { AllowAutoRedirect = false }) { }

        // The injected handler is used by offline tests and must not follow redirects itself.
        internal BootstrapDownloader(HttpMessageHandler handler)
        {
            client = new HttpClient(handler);
            client.Timeout = Timeout.InfiniteTimeSpan;
        }

        public async Task DownloadAsync(Uri uri, string sha512, long maximumBytes,
            Stream destination, IProgress<long> progress, CancellationToken cancellationToken)
        {
            ExpectedLength = null;
            if (maximumBytes <= 0 || maximumBytes > 1024L * 1024 * 1024)
                throw new ArgumentOutOfRangeException("maximumBytes");
            if (!destination.CanSeek || !destination.CanRead || !destination.CanWrite || destination.Length != 0)
                throw new ArgumentException("A new readable, writable, seekable staging stream is required.", "destination");
            using (CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeout.CancelAfter(TimeSpan.FromMinutes(5));
                CancellationToken token = timeout.Token;
                try
                {
                    for (int redirects = 0; redirects <= 5; redirects++)
                    {
                        BootstrapDependencyPolicy.ValidateDownloadUri(uri);
                        using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, uri))
                        using (HttpResponseMessage response = await client.SendAsync(request,
                            HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
                        {
                            int status = (int)response.StatusCode;
                            if (status == 301 || status == 302 || status == 303 || status == 307 || status == 308)
                            {
                                var location = response.Headers.Location;
                                if (location == null) throw new InvalidDataException("Dependency redirect has no location.");
                                uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                                BootstrapDependencyPolicy.ValidateDownloadUri(uri);
                                continue;
                            }
                            if (response.StatusCode != HttpStatusCode.OK)
                                throw new HttpRequestException("Dependency download failed: HTTP " + status + ".");
                            long? expectedLength = response.Content.Headers.ContentLength;
                            ExpectedLength = expectedLength;
                            if (expectedLength.HasValue && expectedLength.Value > maximumBytes)
                                throw new InvalidDataException("Dependency exceeds its download size limit.");
                            using (Stream source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                            {
                                byte[] buffer = new byte[64 * 1024];
                                long total = 0;
                                int count;
                                while ((count = await source.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false)) != 0)
                                {
                                    total += count;
                                    if (total > maximumBytes) throw new InvalidDataException("Dependency exceeds its download size limit.");
                                    await destination.WriteAsync(buffer, 0, count, token).ConfigureAwait(false);
                                    if (progress != null) progress.Report(total);
                                }
                                if (expectedLength.HasValue && total != expectedLength.Value)
                                    throw new InvalidDataException("Dependency download is incomplete.");
                            }
                            token.ThrowIfCancellationRequested();
                            destination.Position = 0;
                            BootstrapDependencyPolicy.VerifySha512(destination, sha512);
                            token.ThrowIfCancellationRequested();
                            destination.Position = 0;
                            return;
                        }
                    }
                    throw new InvalidDataException("Too many dependency download redirects.");
                }
                catch
                {
                    // A failed download must never leave a seemingly usable executable.
                    destination.SetLength(0);
                    destination.Position = 0;
                    throw;
                }
            }
        }

        public void Dispose() { client.Dispose(); }
    }
}
