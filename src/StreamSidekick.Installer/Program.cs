using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;

const string productName = "John's Twitch Handler";
const string payloadResourceName = "JohnsTwitchHandler.Payload.zip";

try
{
    if (!OperatingSystem.IsWindows())
        throw new PlatformNotSupportedException("This installer is for Windows only.");

    // Release verification can extract the embedded payload into a disposable folder without
    // stopping John's running copy, creating shortcuts, or launching a browser.
    var verificationMode = args.Length == 2 && args[0].Equals("--verify-install", StringComparison.OrdinalIgnoreCase);
    var installRoot = verificationMode
        ? Path.GetFullPath(args[1])
        : Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "JohnsTwitchHandler",
            "Tester");

    // Updating an open copy would leave a mixture of old and new files. Stop only the Handler,
    // wait briefly for Windows to release it, and leave OBS and every other streaming app alone.
    foreach (var process in verificationMode ? [] : Process.GetProcessesByName("JohnsTwitchHandler"))
    {
        try
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
        }
        catch
        {
            // A process can finish between discovery and Kill. Extraction below reports a clear
            // error if a genuinely locked file remains.
        }
        finally
        {
            process.Dispose();
        }
    }

    Directory.CreateDirectory(installRoot);
    using var payload = typeof(Program).Assembly.GetManifestResourceStream(payloadResourceName)
        ?? throw new InvalidOperationException("The application files are missing from this installer.");
    using var archive = new ZipArchive(payload, ZipArchiveMode.Read);

    // Resolve each destination before writing so a malformed archive cannot escape the dedicated
    // local installation folder.
    var rootWithSeparator = Path.GetFullPath(installRoot) + Path.DirectorySeparatorChar;
    foreach (var entry in archive.Entries)
    {
        var destination = Path.GetFullPath(Path.Combine(installRoot, entry.FullName));
        if (!destination.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Unsafe installer entry: {entry.FullName}");

        if (string.IsNullOrEmpty(entry.Name))
        {
            Directory.CreateDirectory(destination);
            continue;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        entry.ExtractToFile(destination, overwrite: true);
    }

    var launcherPath = Path.Combine(installRoot, "Open John's Twitch Handler.cmd");
    if (!File.Exists(launcherPath))
        throw new FileNotFoundException("The Handler launcher was not installed.", launcherPath);

    if (!verificationMode)
    {
        CreateShortcut(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), $"{productName}.lnk"),
            launcherPath,
            installRoot);
        CreateShortcut(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), $"{productName}.lnk"),
            launcherPath,
            installRoot);

        // Opening the same installer again acts as a clean update: files are refreshed, shortcuts
        // are repaired, the previous Handler process is replaced, and the newest dashboard opens.
        Process.Start(new ProcessStartInfo(launcherPath)
        {
            UseShellExecute = true,
            WorkingDirectory = installRoot,
            WindowStyle = ProcessWindowStyle.Hidden
        });
    }
}
catch (Exception exception)
{
    MessageBoxW(
        IntPtr.Zero,
        $"{productName} could not be installed.\n\n{exception.GetBaseException().Message}\n\nNo OBS, Twitch, or Spotify settings were removed.",
        $"{productName} Setup",
        0x00000010);
    Environment.ExitCode = 1;
}

static void CreateShortcut(string shortcutPath, string targetPath, string workingDirectory)
{
    Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath)!);
    var shellType = Type.GetTypeFromProgID("WScript.Shell")
        ?? throw new PlatformNotSupportedException("Windows shortcut support is unavailable.");
    dynamic? shell = null;
    dynamic? shortcut = null;
    try
    {
        shell = Activator.CreateInstance(shellType);
        shortcut = shell!.CreateShortcut(shortcutPath);
        shortcut.TargetPath = targetPath;
        shortcut.WorkingDirectory = workingDirectory;
        shortcut.IconLocation = Path.Combine(workingDirectory, "JohnsTwitchHandler.exe");
        shortcut.Description = "Open John's Twitch Handler";
        shortcut.Save();
    }
    finally
    {
        if (shortcut is not null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
        if (shell is not null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
    }
}

[DllImport("user32.dll", CharSet = CharSet.Unicode)]
static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
