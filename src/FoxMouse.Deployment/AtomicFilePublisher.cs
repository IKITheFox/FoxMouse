namespace FoxMouse.Deployment;

public static class AtomicFilePublisher
{
    public static void WriteAllText(string path, string contents)
    {
        string destination = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(destination) ??
            throw new ArgumentException("The atomic file destination needs a parent directory.", nameof(path));
        Directory.CreateDirectory(directory);
        string candidate = Path.Combine(directory, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (FileStream stream = new(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (StreamWriter writer = new(
                stream,
                new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                bufferSize: 1024,
                leaveOpen: true))
            {
                writer.Write(contents);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            Exception? lastFailure = null;
            DateTime deadline = DateTime.UtcNow.AddSeconds(5);
            do
            {
                try
                {
                    File.Move(candidate, destination, overwrite: true);
                    lastFailure = null;
                    break;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    lastFailure = exception;
                    Thread.Sleep(10);
                }
            }
            while (DateTime.UtcNow < deadline);

            if (lastFailure is not null)
            {
                throw new IOException("Could not atomically publish the file.", lastFailure);
            }
        }
        finally
        {
            if (File.Exists(candidate))
            {
                File.Delete(candidate);
            }
        }
    }
}
