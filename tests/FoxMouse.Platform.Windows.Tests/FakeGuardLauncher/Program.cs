using System.Diagnostics;

const string guardPathVariable = "FOXMOUSE_TEST_GUARD_EXECUTABLE";
string? guardExecutable = Environment.GetEnvironmentVariable(guardPathVariable);
if (string.IsNullOrWhiteSpace(guardExecutable) || !File.Exists(guardExecutable))
{
    return 20;
}

ProcessStartInfo startInfo = new()
{
    FileName = guardExecutable,
    UseShellExecute = false,
    CreateNoWindow = true,
};
foreach (string argument in args)
{
    startInfo.ArgumentList.Add(argument);
}

// This launcher exists only in the test tree and makes it impossible for the
// process-level GuardClient tests to start the real cursor backend by mistake.
startInfo.ArgumentList.Add("--fake");
string? extraArgument = Environment.GetEnvironmentVariable("FOXMOUSE_TEST_GUARD_EXTRA_ARGUMENT");
if (!string.IsNullOrWhiteSpace(extraArgument))
{
    startInfo.ArgumentList.Add(extraArgument);
}

using Process? guard = Process.Start(startInfo);
if (guard is null)
{
    return 21;
}

await guard.WaitForExitAsync().ConfigureAwait(false);
return guard.ExitCode;
