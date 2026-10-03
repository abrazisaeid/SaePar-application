using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

internal static class Program
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr window, string text, string caption, uint flags);

    private static int Main(string[] args)
    {
        bool verifyOnly = args.SequenceEqual(new[] { "--verify" });
        try
        {
            if (!OperatingSystem.IsWindows() || !Environment.Is64BitOperatingSystem)
                throw new InvalidOperationException("This release requires 64-bit Windows.");
            var assembly = Assembly.GetExecutingAssembly();
            using var payload = assembly.GetManifestResourceStream("Payload.windows.zip")
                ?? throw new InvalidDataException("The Windows package is missing.");
            var expected = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .Single(a => a.Key == "PayloadSha256").Value;
            if (!Convert.ToHexString(SHA256.HashData(payload)).Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The package is damaged. Download it again.");
            payload.Position = 0;
            using var archive = new ZipArchive(payload, ZipArchiveMode.Read);
            if (archive.GetEntry("SaeParTunnel.App.exe") is null || archive.GetEntry("xray.exe") is null)
                throw new InvalidDataException("The application or engine is missing.");
            if (verifyOnly)
            {
                foreach (var entry in archive.Entries.Where(e => e.Name.Length > 0))
                {
                    using var content = entry.Open();
                    content.CopyTo(Stream.Null);
                }
                Console.WriteLine($"Verified embedded ZIP: {archive.Entries.Count} entries; SHA256 {expected}.");
                return 0;
            }

            var version = assembly.GetName().Version!;
            var label = $"{version.Major}.{version.Minor}.{version.Build}";
            if (MessageBox(IntPtr.Zero, $"Install SaePar Tunnel {label} and create Desktop and Start menu shortcuts?", "SaePar Tunnel", 0x21) != 1)
                return 0;
            var root = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "SaeParTunnel"));
            var target = Path.GetFullPath(Path.Combine(root, label));
            if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Invalid installation directory.");
            Directory.CreateDirectory(target);
            foreach (var entry in archive.Entries)
            {
                var file = Path.GetFullPath(Path.Combine(target, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
                if (!file.StartsWith(target + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Invalid package entry.");
                if (entry.Name.Length == 0) { Directory.CreateDirectory(file); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                entry.ExtractToFile(file, overwrite: true);
            }
            var exe = Path.Combine(target, "SaeParTunnel.App.exe");
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
            try
            {
                foreach (var folder in new[] { Environment.SpecialFolder.Programs, Environment.SpecialFolder.DesktopDirectory })
                {
                    var path = Environment.GetFolderPath(folder);
                    if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) continue;
                    dynamic shortcut = shell.CreateShortcut(Path.Combine(path, "SaePar Tunnel.lnk"));
                    try
                    {
                        shortcut.TargetPath = exe;
                        shortcut.WorkingDirectory = target;
                        shortcut.IconLocation = exe + ",0";
                        shortcut.Save();
                    }
                    finally { Marshal.FinalReleaseComObject(shortcut); }
                }
            }
            finally { Marshal.FinalReleaseComObject(shell); }
            MessageBox(IntPtr.Zero, "Installation complete. Open SaePar Tunnel from your Desktop or Start menu.", "SaePar Tunnel", 0x40);
            return 0;
        }
        catch (Exception error)
        {
            if (verifyOnly) Console.Error.WriteLine(error.Message);
            else MessageBox(IntPtr.Zero, "Installation failed. Close SaePar Tunnel and try again.\n\n" + error.Message, "SaePar Tunnel", 0x10);
            return 1;
        }
    }
}
