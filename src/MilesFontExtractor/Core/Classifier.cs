using System.Collections.Concurrent;
using System.Security.AccessControl;
using System.Security.Principal;
using MilesFontExtractor.Models;
using Microsoft.Win32;

namespace MilesFontExtractor.Core;

/// <summary>
/// Decides whether a font belongs to Windows, to an installed program, or is a loose font that is
/// safe to extract. A font is only excluded when there is positive evidence (an installed program's
/// folder, Windows' own ownership, ...). Anything ambiguous goes to "Needs review" and is never
/// silently dropped or auto-selected.
/// </summary>
public sealed class Classifier
{
    private const string TrustedInstallerSid = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";

    private static readonly HashSet<string> PackageMarkers = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", "site-packages", "dist-packages", "bower_components", ".gradle", ".nuget", ".pub-cache", "WinSxS",
        ".next", ".nuxt", ".svelte-kit", ".turbo", ".parcel-cache", ".angular"
    };

    private readonly Dictionary<string, string> _programRoots = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _programFilesDirs = new();
    private readonly List<string> _userFolders = new();
    private readonly ConcurrentDictionary<string, bool> _exeCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _winDir, _winFonts, _userFonts, _localAppData, _roamingAppData, _programData, _temp;

    public int ProgramRootCount => _programRoots.Count;

    public Classifier()
    {
        _winDir = Norm(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        _winFonts = Path.Combine(_winDir, "Fonts");
        _localAppData = Norm(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        _roamingAppData = Norm(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
        _programData = Norm(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
        _userFonts = Path.Combine(_localAppData, "Microsoft", "Windows", "Fonts");
        _temp = Path.Combine(_localAppData, "Temp");

        string profile = Norm(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        _userFolders.Add(profile);
        foreach (var f in new[]
        {
            Environment.SpecialFolder.Desktop, Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.MyPictures,
            Environment.SpecialFolder.MyVideos, Environment.SpecialFolder.MyMusic
        })
        {
            var p = Environment.GetFolderPath(f);
            if (!string.IsNullOrEmpty(p)) _userFolders.Add(Norm(p));
        }
        _userFolders.Add(Path.Combine(profile, "Downloads"));

        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed) continue;
            foreach (var n in new[] { "Program Files", "Program Files (x86)" })
            {
                var d = Path.Combine(drive.Name, n);
                if (Directory.Exists(d)) _programFilesDirs.Add(Norm(d));
            }
        }

        AddRegistryRoots();
    }

    // ---------------------------------------------------------------------------------------------

    public (FontCategory category, string reason) Classify(string path, FontInfo info)
    {
        string? dir = Path.GetDirectoryName(path);
        if (dir == null) return (FontCategory.Loose, "Not part of any program");

        // 1. The Windows font folder: separate Windows' own fonts from ones the user added.
        if (dir.Equals(_winFonts, StringComparison.OrdinalIgnoreCase))
            return ClassifyWindowsFont(path, info);

        // 2. Fonts installed "just for me" through Windows.
        if (IsUnder(path, _userFonts))
            return (FontCategory.WindowsUser, "Installed for your account in Windows");

        // 3. Everything else inside the Windows folder is part of the OS.
        if (IsUnder(path, _winDir))
            return (FontCategory.System, "Part of Windows");

        // 4. Temporary folders usually hold extracted installers or archives: show, don't decide.
        if (IsUnder(path, _temp))
            return (FontCategory.Review, "Found in a temporary folder");

        // 5. Inside the folder of an installed program.
        for (var d = dir; !string.IsNullOrEmpty(d); d = Path.GetDirectoryName(d))
            if (_programRoots.TryGetValue(d, out var prog))
                return (FontCategory.Program, $"Belongs to {prog}");

        foreach (var pf in _programFilesDirs)
            if (IsUnder(path, pf)) return (FontCategory.Program, $"Belongs to {FirstSegment(path, pf, skipWindowsApps: true)}");

        if (IsUnder(path, _programData))
            return (FontCategory.Program, $"App data of {FirstSegment(path, _programData)}");

        foreach (var appData in new[] { _localAppData, _roamingAppData })
        {
            if (!IsUnder(path, appData)) continue;
            string app = FirstSegment(path, appData);
            if (app.Equals("Programs", StringComparison.OrdinalIgnoreCase) ||
                app.Equals("Packages", StringComparison.OrdinalIgnoreCase))
                app = FirstSegment(path, Path.Combine(appData, app));
            return (FontCategory.Program, $"App data of {app}");
        }

        // 6. Dependencies bundled by development tools.
        for (var d = dir; !string.IsNullOrEmpty(d); d = Path.GetDirectoryName(d))
        {
            var name = Path.GetFileName(d);
            if (name.Length > 0 && PackageMarkers.Contains(name))
                return (FontCategory.Program, $"Bundled dependency ({name})");
        }

        // 7. Sitting right next to an application (portable apps, game folders): ask the user.
        if (LooksLikeApplicationFolder(dir))
            return (FontCategory.Review, "Next to an application (.exe) - may belong to it");

        return (FontCategory.Loose, "Not part of Windows or any installed program");
    }

    private (FontCategory, string) ClassifyWindowsFont(string path, FontInfo info)
    {
        // Fonts that shipped with Windows are owned by TrustedInstaller; fonts a person installs are not.
        bool? ti = IsOwnedByTrustedInstaller(path);
        if (ti == true) return (FontCategory.System, "Shipped with Windows");

        if (info.Copyright.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) ||
            info.Vendor.Contains("Microsoft", StringComparison.OrdinalIgnoreCase))
            return (FontCategory.System, "Microsoft font (Windows / Office)");

        if (ti == null) return (FontCategory.System, "Protected Windows font");

        // Not Windows' own, but Office and other installers also drop fonts here with the same owner as a
        // manual install, so ownership can't prove "installed by you". Ask instead of guessing.
        return (FontCategory.Review, "In the Windows font folder but not part of Windows - may belong to Office or another program");
    }

    private static bool? IsOwnedByTrustedInstaller(string path)
    {
        try
        {
            var sec = new FileInfo(path).GetAccessControl(AccessControlSections.Owner);
            var owner = sec.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            return owner?.Value == TrustedInstallerSid;
        }
        catch
        {
            return null;
        }
    }

    private bool LooksLikeApplicationFolder(string fontDir)
    {
        var d = fontDir;
        for (int i = 0; i < 3 && !string.IsNullOrEmpty(d); i++)
        {
            if (IsStopFolder(d)) return false;
            if (HasExe(d)) return true;
            d = Path.GetDirectoryName(d);
        }
        return false;
    }

    private bool IsStopFolder(string dir)
    {
        if (Path.GetPathRoot(dir)?.TrimEnd('\\').Equals(dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) == true) return true;
        foreach (var u in _userFolders)
            if (u.Equals(dir, StringComparison.OrdinalIgnoreCase) || IsUnder(u, dir)) return true;
        return false;
    }

    private bool HasExe(string dir) => _exeCache.GetOrAdd(dir, static d =>
    {
        try
        {
            return Directory.EnumerateFiles(d, "*.exe", new EnumerationOptions { IgnoreInaccessible = true }).Any();
        }
        catch
        {
            return false;
        }
    });

    // ---------------------------------------------------------------------------------------------

    private void AddRegistryRoots()
    {
        var views = new[]
        {
            (RegistryHive.LocalMachine, RegistryView.Registry64),
            (RegistryHive.LocalMachine, RegistryView.Registry32),
            (RegistryHive.CurrentUser, RegistryView.Registry64)
        };

        foreach (var (hive, view) in views)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall == null) continue;

                foreach (var sub in uninstall.GetSubKeyNames())
                {
                    try
                    {
                        using var k = uninstall.OpenSubKey(sub);
                        var name = (k?.GetValue("DisplayName") as string)?.Trim();
                        if (string.IsNullOrEmpty(name)) continue;

                        TryAddRoot((k!.GetValue("InstallLocation") as string)?.Trim().Trim('"'), name);

                        var icon = (k.GetValue("DisplayIcon") as string)?.Trim();
                        if (!string.IsNullOrEmpty(icon))
                        {
                            icon = icon.Trim('"');
                            int comma = icon.LastIndexOf(',');
                            if (comma > 2) icon = icon[..comma].Trim('"');
                            TryAddRoot(Path.GetDirectoryName(icon), name);
                        }
                    }
                    catch { /* unreadable entry: skip */ }
                }
            }
            catch { /* hive unavailable */ }
        }
    }

    /// <summary>Adds an installed program's folder, but refuses folders that are too broad to mean "one program".</summary>
    private void TryAddRoot(string? dir, string program)
    {
        if (string.IsNullOrWhiteSpace(dir)) return;
        try
        {
            dir = Norm(dir);
            if (!Directory.Exists(dir)) return;
            if (Path.GetPathRoot(dir)?.TrimEnd('\\').Equals(dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) == true) return;
            if (dir.Equals(_winDir, StringComparison.OrdinalIgnoreCase) || IsUnder(dir, _winDir)) return;
            if (dir.Equals(_programData, StringComparison.OrdinalIgnoreCase)) return;
            if (_programFilesDirs.Any(p => p.Equals(dir, StringComparison.OrdinalIgnoreCase))) return;
            // A folder that contains the user's Desktop/Documents/etc. can't be "one program".
            if (_userFolders.Any(u => u.Equals(dir, StringComparison.OrdinalIgnoreCase) || IsUnder(u, dir))) return;
            _programRoots.TryAdd(dir, program);
        }
        catch { /* invalid path */ }
    }

    // ---------------------------------------------------------------------------------------------

    private static string Norm(string p) => Path.GetFullPath(p).TrimEnd('\\', '/');

    private static bool IsUnder(string path, string root) =>
        path.Length > root.Length && path.StartsWith(root, StringComparison.OrdinalIgnoreCase) && path[root.Length] == '\\';

    private static string FirstSegment(string path, string root, bool skipWindowsApps = false)
    {
        var parts = path[(root.Length + 1)..].Split('\\');
        if (skipWindowsApps && parts.Length > 2 && parts[0].Equals("WindowsApps", StringComparison.OrdinalIgnoreCase)) return parts[1];
        return parts[0];
    }
}
