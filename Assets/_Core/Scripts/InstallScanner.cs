using System.IO;
using System.Linq;
using System.Text;

namespace ArcadeLauncher.Core
{
    public static class InstallScanner
    {
        // Installer/redist/crash-handler residue commonly bundled with itch.io archives and engine exports.
        // Matched as case-insensitive filename prefixes — never treated as the game executable.
        static readonly string[] _excludedPrefixes =
        {
            "unins", "uninstall", "setup", "install",
            "vc_redist", "vcredist", "dxsetup", "directx",
            "ueprereqsetup", "crashreportclient", "crashpad", "unitycrashhandler",
            "nodos", "_commonredist"
        };

        /// <summary>
        /// Resolves the actual executable filename inside <paramref name="folder"/>.
        /// Honors <paramref name="preferredName"/> when it exists on disk; otherwise scans the folder
        /// (top level only) and applies heuristics so engines like Godot keep the .exe ↔ .pck pairing intact.
        /// Returns the filename (no directory) or null if nothing usable was found.
        /// </summary>
        public static string FindExecutable(string folder, string preferredName, string entryId, string title)
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) {
                return null;
            }

            if (!string.IsNullOrEmpty(preferredName)) {
                string preferredPath = Path.Combine(folder, preferredName);
                if (File.Exists(preferredPath)) {
                    return preferredName;
                }
            }

            string[] candidates;
            try {
                candidates = Directory.GetFiles(folder, "*.exe", SearchOption.TopDirectoryOnly);
            } catch (IOException) {
                return null;
            } catch (System.UnauthorizedAccessException) {
                return null;
            }

            string[] filtered = candidates
                .Where(p => !IsExcluded(Path.GetFileName(p)))
                .ToArray();

            if (filtered.Length == 0) {
                return null;
            }
            if (filtered.Length == 1) {
                return Path.GetFileName(filtered[0]);
            }

            string idKey = Normalize(entryId);
            string idMatch = filtered.FirstOrDefault(p => Normalize(Path.GetFileNameWithoutExtension(p)) == idKey);
            if (idMatch != null) {
                return Path.GetFileName(idMatch);
            }

            string titleKey = Normalize(title);
            string titleMatch = filtered.FirstOrDefault(p => Normalize(Path.GetFileNameWithoutExtension(p)) == titleKey);
            if (titleMatch != null) {
                return Path.GetFileName(titleMatch);
            }

            // Fallback: largest .exe wins — game binaries dwarf any redist that slipped past the filter.
            FileInfo largest = filtered
                .Select(p => new FileInfo(p))
                .OrderByDescending(fi => fi.Length)
                .First();
            return largest.Name;
        }

        static bool IsExcluded(string fileName)
        {
            string lower = fileName.ToLowerInvariant();
            return _excludedPrefixes.Any(prefix => lower.StartsWith(prefix));
        }

        static string Normalize(string s)
        {
            if (string.IsNullOrEmpty(s)) {
                return "";
            }
            var builder = new StringBuilder(s.Length);
            foreach (char c in s) {
                if (char.IsLetterOrDigit(c)) {
                    builder.Append(char.ToLowerInvariant(c));
                }
            }
            return builder.ToString();
        }
    }
}
