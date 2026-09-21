using System.Diagnostics;

namespace StreamSidekick.Services;

/// <summary>
/// Small process helpers shared by preflight checks. <see cref="Process.GetProcessesByName(string)"/>
/// returns disposable handles, so centralizing this check prevents the dashboard's frequent polling
/// from slowly accumulating native resources.
/// </summary>
internal static class ProcessUtilities
{
    public static bool IsAnyRunning(params string[] processNames)
    {
        foreach (var processName in processNames)
        {
            var processes = Process.GetProcessesByName(processName);
            try
            {
                if (processes.Length > 0) return true;
            }
            finally
            {
                foreach (var process in processes) process.Dispose();
            }
        }

        return false;
    }
}
