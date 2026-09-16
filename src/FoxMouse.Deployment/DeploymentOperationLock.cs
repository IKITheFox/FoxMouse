using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace FoxMouse.Deployment;

internal sealed class DeploymentOperationLock : IDisposable
{
    private static readonly ConcurrentDictionary<string, byte> ActiveLocks = new(StringComparer.Ordinal);
    private readonly Mutex _mutex;
    private readonly string _name;
    private bool _acquired;

    private DeploymentOperationLock(Mutex mutex, string name, bool acquired)
    {
        _mutex = mutex;
        _name = name;
        _acquired = acquired;
    }

    internal static DeploymentOperationLock Acquire(string installRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installRoot);
        // Installation roots are selectable, so a root-keyed mutex would allow
        // two concurrent FoxMouse transactions to mutate the shared ARP, cache,
        // settings and shortcut state. Use one product lock per Windows user.
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        string userIdentity = identity.User?.Value ?? Environment.UserName;
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(userIdentity.ToUpperInvariant()));
        string name = @"Local\FoxMouse.Maintenance.User." + Convert.ToHexString(digest.AsSpan(0, 12));
        if (!ActiveLocks.TryAdd(name, 0))
        {
            throw new InvalidOperationException("Another FoxMouse installation or maintenance operation is already running.");
        }

        Mutex? mutex = null;
        try
        {
            mutex = new Mutex(initiallyOwned: false, name);
            bool acquired;
            try
            {
                acquired = mutex.WaitOne(TimeSpan.Zero);
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }

            if (!acquired)
            {
                mutex.Dispose();
                mutex = null;
                throw new InvalidOperationException("Another FoxMouse installation or maintenance operation is already running.");
            }

            return new DeploymentOperationLock(mutex, name, acquired: true);
        }
        catch
        {
            mutex?.Dispose();
            _ = ActiveLocks.TryRemove(name, out _);
            throw;
        }
    }

    public void Dispose()
    {
        if (_acquired)
        {
            _mutex.ReleaseMutex();
            _acquired = false;
        }

        _mutex.Dispose();
        _ = ActiveLocks.TryRemove(_name, out _);
    }
}
