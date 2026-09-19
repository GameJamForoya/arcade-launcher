using System;
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
                .Where(p => !IsExcludedExecutableName(Path.GetFileName(p)))
                .ToArray();

            if (filtered.Length == 0) {
                return null;
            }
            if (filtered.Length == 1) {
                return Path.GetFileName(filtered[0]);
            }

            string idKey = NormalizeForNameMatch(entryId);
            string idMatch = filtered.FirstOrDefault(p => NormalizeForNameMatch(Path.GetFileNameWithoutExtension(p)) == idKey);
            if (idMatch != null) {
                return Path.GetFileName(idMatch);
            }

            string titleKey = NormalizeForNameMatch(title);
            string titleMatch = filtered.FirstOrDefault(p => NormalizeForNameMatch(Path.GetFileNameWithoutExtension(p)) == titleKey);
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

        /// <summary>
        /// True when <paramref name="fileName"/> is installer/redist/crash-handler residue rather than a
        /// game executable. Public so curator tooling can apply the same filter to archive entry names
        /// without extracting the archive.
        /// </summary>
        public static bool IsExcludedExecutableName(string fileName)
        {
            string lower = fileName.ToLowerInvariant();
            return _excludedPrefixes.Any(prefix => lower.StartsWith(prefix));
        }

        /// <summary>
        /// True for archive metadata that ships alongside the real payload — macOS's "__MACOSX"
        /// folder, "._*" AppleDouble files, ".DS_Store" and "Thumbs.db". These must never count as
        /// content: a "__MACOSX" folder next to the game folder blocks wrapper-flattening, and a
        /// "._MyGame.exe" AppleDouble can shadow the real executable during name matching.
        /// </summary>
        public static bool IsArchiveJunkName(string name)
        {
            if (string.IsNullOrEmpty(name)) {
                return false;
            }
            if (name.StartsWith("._", StringComparison.Ordinal)) {
                return true;
            }
            return string.Equals(name, "__MACOSX", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, ".DS_Store", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Thumbs.db", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Deletes top-level archive junk (see <see cref="IsArchiveJunkName"/>) from
        /// <paramref name="folder"/>. Call before AND after wrapper-flattening: before, so junk does
        /// not block the single-wrapper check; after, because the wrapper may have carried its own.
        /// <paramref name="onNote"/> receives one message per deletion or failure.
        /// </summary>
        public static void DeleteArchiveJunk(string folder, Action<string> onNote)
        {
            string[] directories;
            string[] files;
            try {
                directories = Directory.GetDirectories(folder);
                files = Directory.GetFiles(folder);
            } catch (IOException e) {
                onNote?.Invoke($"could not scan for archive junk — {e.Message}");
                return;
            } catch (UnauthorizedAccessException e) {
                onNote?.Invoke($"could not scan for archive junk — {e.Message}");
                return;
            }

            foreach (string directory in directories) {
                string name = Path.GetFileName(directory);
                if (!IsArchiveJunkName(name)) {
                    continue;
                }
                try {
                    Directory.Delete(directory, true);
                    onNote?.Invoke($"removed archive junk folder '{name}'");
                } catch (IOException e) {
                    onNote?.Invoke($"could not remove junk folder '{name}' — {e.Message}");
                } catch (UnauthorizedAccessException e) {
                    onNote?.Invoke($"could not remove junk folder '{name}' — {e.Message}");
                }
            }

            foreach (string file in files) {
                string name = Path.GetFileName(file);
                if (!IsArchiveJunkName(name)) {
                    continue;
                }
                try {
                    File.Delete(file);
                    onNote?.Invoke($"removed archive junk file '{name}'");
                } catch (IOException e) {
                    onNote?.Invoke($"could not remove junk file '{name}' — {e.Message}");
                } catch (UnauthorizedAccessException e) {
                    onNote?.Invoke($"could not remove junk file '{name}' — {e.Message}");
                }
            }
        }

        /// <summary>
        /// Folds a name to lowercase letters and digits only, so ids/titles match executable names
        /// regardless of spacing, casing and punctuation. Public so curator tooling applies the
        /// exact same matching rule to archive entry names.
        /// </summary>
        public static string NormalizeForNameMatch(string s)
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
