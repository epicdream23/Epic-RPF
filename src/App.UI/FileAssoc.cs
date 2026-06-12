using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace App.UI;

/// <summary>
/// Registers the file types Epic RPF can open under the current user (HKCU\Software\
/// Classes — no admin needed) so double-clicking one launches the app with the file.
/// Idempotent: it only rewrites the keys when they don't already point at this exe.
/// </summary>
internal static class FileAssoc
{
    private const string FileProgId = "EpicRpf.File";   // generic resources/metas — app icon
    private const string EpicProgId = "EpicRpf.Epic";   // our own .epic format — custom package icon

    // Generic extensions the app can view/edit (resources, metas, scaleform). The .epic
    // format is handled separately so it can carry its own icon + be the default handler.
    private static readonly string[] Exts =
    {
        ".ydr", ".ydd", ".yft", ".ytd", ".ypt", ".ymt", ".ymap", ".ytyp", ".ymf",
        ".meta", ".pso", ".rbf", ".gfx", ".ynd", ".ycd", ".ybn", ".awc", ".rel",
        ".dat", ".cut",
    };

    public static void EnsureRegistered()
    {
        try
        {
            string exe = Process.GetCurrentProcess().MainModule?.FileName ?? "";
            if (string.IsNullOrEmpty(exe)) return;
            string command = $"\"{exe}\" \"%1\"";

            // The custom .epic icon ships next to the exe (wwwroot is copied / published).
            string epicIco = Path.Combine(Path.GetDirectoryName(exe) ?? "", "wwwroot", "icons", "epic.ico");
            string epicIconValue = $"\"{epicIco}\",0";

            // Skip only when BOTH the open command AND the custom .epic icon are already current
            // (so an upgrade that adds/changes the icon still re-registers once).
            using (var cmdKey = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{FileProgId}\shell\open\command"))
            using (var icoKey = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{EpicProgId}\DefaultIcon"))
                if (cmdKey?.GetValue(null) as string == command && icoKey?.GetValue(null) as string == epicIconValue)
                    return;

            // ProgId for generic game files — uses the app's own icon.
            using (var prog = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{FileProgId}"))
            {
                prog.SetValue(null, "Epic RPF file");
                using (var icon = prog.CreateSubKey("DefaultIcon")) icon.SetValue(null, $"\"{exe}\",0");
                using var cmd = prog.CreateSubKey(@"shell\open\command");
                cmd.SetValue(null, command);
            }

            // ProgId for our own .epic packages — distinct package icon.
            using (var prog = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{EpicProgId}"))
            {
                prog.SetValue(null, "Epic RPF extension");
                using (var icon = prog.CreateSubKey("DefaultIcon"))
                    icon.SetValue(null, File.Exists(epicIco) ? epicIconValue : $"\"{exe}\",0");
                using var cmd = prog.CreateSubKey(@"shell\open\command");
                cmd.SetValue(null, command);
            }

            // Generic extensions: offer us in "Open with" but don't hijack the default.
            foreach (var ext in Exts)
                using (var k = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ext}\OpenWithProgids"))
                    k.SetValue(FileProgId, Array.Empty<byte>(), RegistryValueKind.None);

            // .epic is our own format: make us the default handler and use the package icon.
            using (var k = Registry.CurrentUser.CreateSubKey($@"Software\Classes\.epic\OpenWithProgids"))
                k.SetValue(EpicProgId, Array.Empty<byte>(), RegistryValueKind.None);
            using (var def = Registry.CurrentUser.CreateSubKey($@"Software\Classes\.epic"))
                def.SetValue(null, EpicProgId);

            SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);   // SHCNE_ASSOCCHANGED
        }
        catch { /* association is a convenience; never block startup */ }
    }

    [DllImport("shell32.dll")] private static extern void SHChangeNotify(int eventId, uint flags, IntPtr a, IntPtr b);
}
