using System.Runtime.ExceptionServices;

namespace FoxMouse.Deployment.Tests;

internal static class StaTestThread
{
    internal static void Run(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        Exception? failure = null;
        Thread thread = new(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        })
        {
            IsBackground = true,
            Name = "FoxMouse maintenance UI test STA",
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        if (!thread.Join(TimeSpan.FromSeconds(15)))
        {
            throw new TimeoutException("The FoxMouse maintenance UI test STA did not finish within 15 seconds.");
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}

