using System;
using System.IO;
using System.Net;
using System.Threading;

namespace FoxMouse.Bootstrap
{
    // Diagnostic entry point: download and verify only, never execute a payload.
    internal static class BootstrapDownloadProbe
    {
        private static int Main(string[] args)
        {
            if (args.Length != 3) return 2;
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            try
            {
                using (BootstrapDownloader downloader = new BootstrapDownloader())
                using (FileStream destination = new FileStream(args[2], FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read))
                {
                    downloader.DownloadAsync(new Uri(args[0]), args[1], 300L * 1024 * 1024,
                        destination, null, CancellationToken.None).GetAwaiter().GetResult();
                    destination.Flush(true);
                    Console.WriteLine("Verified bytes: " + destination.Length);
                }
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
                return 1;
            }
        }
    }
}
