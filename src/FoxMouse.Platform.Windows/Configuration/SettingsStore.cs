using System.Text;
using System.Text.Json;
using FoxMouse.Core;

namespace FoxMouse.Platform.Windows.Configuration;

public sealed class SettingsStore
{
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private long _nextSaveSequence;
    private long _lastCommittedSequence;

    public SettingsStore(string? rootDirectory = null)
    {
        RootDirectory = rootDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FoxMouse");
        SettingsPath = Path.Combine(RootDirectory, "settings.json");
    }

    public string RootDirectory { get; }

    public string SettingsPath { get; }

    public async Task<FoxMouseSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(SettingsPath))
        {
            return FoxMouseSettings.Default;
        }

        try
        {
            string json = await File.ReadAllTextAsync(SettingsPath, Encoding.UTF8, cancellationToken)
                .ConfigureAwait(false);
            return SettingsJson.Deserialize(json);
        }
        catch (JsonException)
        {
            QuarantineBrokenSettings();
            return FoxMouseSettings.Default;
        }
        catch (IOException)
        {
            return FoxMouseSettings.Default;
        }
        catch (UnauthorizedAccessException)
        {
            return FoxMouseSettings.Default;
        }
    }

    public async Task SaveAsync(FoxMouseSettings settings, CancellationToken cancellationToken = default)
    {
        long sequence = Interlocked.Increment(ref _nextSaveSequence);
        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (sequence <= Volatile.Read(ref _lastCommittedSequence))
            {
                return;
            }

            Directory.CreateDirectory(RootDirectory);
            string temporaryPath = $"{SettingsPath}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
            string backupPath = $"{SettingsPath}.bak";
            byte[] data = Encoding.UTF8.GetBytes(SettingsJson.Serialize(settings));
            try
            {
                await using (FileStream stream = new(
                                 temporaryPath,
                                 FileMode.CreateNew,
                                 FileAccess.Write,
                                 FileShare.None,
                                 16 * 1024,
                                 FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    stream.Flush(flushToDisk: true);
                }

                if (File.Exists(SettingsPath))
                {
                    File.Replace(temporaryPath, SettingsPath, backupPath, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(temporaryPath, SettingsPath);
                }

                Volatile.Write(ref _lastCommittedSequence, sequence);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private void QuarantineBrokenSettings()
    {
        try
        {
            Directory.CreateDirectory(RootDirectory);
            string timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmssfff", System.Globalization.CultureInfo.InvariantCulture);
            File.Move(SettingsPath, $"{SettingsPath}.broken-{timestamp}", overwrite: false);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
