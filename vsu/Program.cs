using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VSU
{
    internal class Program
    {
        private static readonly string IGNORED_TXT = "ignored.txt";
        private static readonly string IGNORED_JSON = "ignored.json";
        private static readonly string VERSION_RAPORT_TXT = "version_raport.txt";
        private static readonly string HASHES_JSON = "hashes.json";
        private static readonly string VERSION_HISTORY_JSON = "version_history.json";

        static void Main(string[] args)
        {
            var options = ParseArgs(args);
            if (options.ShowHelp)
            {
                ShowHelp();
                return;
            }

            string rootFolder = options.Path ?? Directory.GetCurrentDirectory();
            string ignoredFilePath = options.IgnoredFile ?? Path.Combine(rootFolder, IGNORED_TXT);
            string reportPath = options.ReportPath ?? Path.Combine(rootFolder, VERSION_RAPORT_TXT);
            string hashFilePath = Path.Combine(rootFolder, HASHES_JSON);
            string historyFilePath = Path.Combine(rootFolder, VERSION_HISTORY_JSON);

            // Load or create hashes.json
            Dictionary<string, string> hashes = File.Exists(hashFilePath)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(hashFilePath))!
                : new Dictionary<string, string>();

            // Load or create history.json
            List<VersionHistory> history = File.Exists(historyFilePath)
                ? JsonSerializer.Deserialize<List<VersionHistory>>(File.ReadAllText(historyFilePath))!
                : new List<VersionHistory>();

            // Load ignored patterns
            if (!File.Exists(ignoredFilePath))
            {
                string jsonIgnored = Path.Combine(rootFolder, IGNORED_JSON);
                if (File.Exists(jsonIgnored)) ignoredFilePath = jsonIgnored;
            }

            var ignoredPatterns = LoadIgnoredPatterns(ignoredFilePath);
            var allowedExtensions = options.Extensions.Any()
                ? new HashSet<string>(options.Extensions, StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(new[] { ".cs" }, StringComparer.OrdinalIgnoreCase);

            var report = new List<string>();
            var allVersions = new List<string>();
            int updatedCount = 0;

            Console.WriteLine($"[INFO] Starting in: {rootFolder}");
            Console.WriteLine($"[INFO] Ignored patterns: {ignoredPatterns.Count}");
            Console.WriteLine($"[INFO] Extensions: {string.Join(", ", allowedExtensions)}");
            Console.WriteLine($"[INFO] Increment mode: {options.IncrementMode}");
            Console.WriteLine();

            foreach (var file in Directory.EnumerateFiles(rootFolder, "*.*", SearchOption.AllDirectories)
                         .Where(f => allowedExtensions.Contains(Path.GetExtension(f))))
            {
                if (IsIgnored(file, ignoredPatterns))
                {
                    Console.WriteLine($"[-] Ignoring: {file}");
                    continue;
                }

                try
                {
                    string versionBefore = GetVersionFromFile(file);
                    string? newVersion = ProcessFile(file, options, hashes);

                    if (newVersion != null)
                    {
                        updatedCount++;
                        report.Add($"{file} -> {newVersion}");
                        allVersions.Add(newVersion);

                        history.Add(new VersionHistory
                        {
                            File = file,
                            OldVersion = string.IsNullOrEmpty(versionBefore) ? "none" : versionBefore,
                            NewVersion = newVersion,
                            Date = DateTime.UtcNow
                        });
                    }
                    else if (!string.IsNullOrEmpty(versionBefore))
                    {
                        allVersions.Add(versionBefore);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ERROR] {file}: {ex.Message}");
                }
            }

            // Save unified hash file
            File.WriteAllText(hashFilePath, JsonSerializer.Serialize(hashes, new JsonSerializerOptions { WriteIndented = true }));

            // Save version history
            File.WriteAllText(historyFilePath, JsonSerializer.Serialize(history, new JsonSerializerOptions { WriteIndented = true }));

            // Calculate overall project version (average from all versions)
            string overallVersion = CalculateAverageVersion(allVersions);

            // Add report summary
            report.Add("\n=== Version Statistics ===");
            report.Add($"Total files processed: {allVersions.Count}");
            report.Add($"Files updated: {updatedCount}");
            report.Add($"Average project version: {overallVersion}");

            File.WriteAllLines(reportPath, report);
            Console.WriteLine($"\n[OK] Finished. Report: {reportPath}");
            Console.WriteLine($"[INFO] Average project version: {overallVersion}");
        }

        // ================================================================
        // Extract version line
        // ================================================================
        internal static string GetVersionFromFile(string filePath)
        {
            if (!File.Exists(filePath)) return "";
            var line = File.ReadLines(filePath).FirstOrDefault(l => l.TrimStart().StartsWith("// Version", StringComparison.OrdinalIgnoreCase));
            if (line == null) return "";
            var match = Regex.Match(line, @"\d+(\.\d+){1,3}");
            return match.Success ? match.Value : "";
        }

        // ================================================================
        // Calculate statistical average version
        // ================================================================
        internal static string CalculateAverageVersion(List<string> versions)
        {
            if (versions.Count == 0) return "0.0.0.0";

            var parsed = versions.Select(v =>
            {
                var p = v.Split('.').Select(x => int.TryParse(x, out int n) ? n : 0).ToArray();
                return p.Length == 4 ? p : p.Concat(Enumerable.Repeat(0, 4 - p.Length)).ToArray();
            }).ToList();

            int[] sums = new int[4];
            foreach (var v in parsed)
                for (int i = 0; i < 4; i++) sums[i] += v[i];

            int[] avg = sums.Select(s => s / versions.Count).ToArray();
            return string.Join('.', avg);
        }

        // ================================================================
        // Process single file
        // ================================================================
        static string? ProcessFile(string filePath, Options options, Dictionary<string, string> hashes)
        {
            var lines = File.Exists(filePath) ? File.ReadAllLines(filePath).ToList() : new List<string>();
            int versionIndex = lines.FindIndex(l => l.TrimStart().StartsWith("// Version", StringComparison.OrdinalIgnoreCase));

            string version = "1.0.0.0";
            if (versionIndex >= 0)
            {
                var match = Regex.Match(lines[versionIndex], @"\d+(\.\d+){1,3}");
                if (match.Success)
                    version = match.Value;
            }

            string contentWithoutVersion = string.Join("\n", lines.Where((_, i) => i != versionIndex));
            string newHash = ComputeHash(contentWithoutVersion);

            hashes.TryGetValue(filePath, out string? oldHash);
            if (oldHash == newHash && versionIndex >= 0)
            {
                Console.WriteLine($"[=] {filePath} – no changes");
                return null;
            }

            version = IncrementVersion(version, options, options.IncrementMode);

            string versionLine = $"// Version: {version}";
            if (versionIndex >= 0)
                lines[versionIndex] = versionLine;
            else
                lines.Insert(0, versionLine);

            File.WriteAllLines(filePath, lines);
            hashes[filePath] = newHash;

            Console.WriteLine($"[↑] {filePath} – new version {version}");
            return version;
        }

        static string ComputeHash(string text)
        {
            using var sha256 = SHA256.Create();
            byte[] hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(text));
            return Convert.ToHexString(hash);
        }

        internal static string IncrementVersion(string version, Options options, string mode)
        {
            var parts = version.Split('.').Select(p => int.TryParse(p, out int n) ? n : 0).ToList();
            while (parts.Count < 4) parts.Add(0);

            // determine which index to increment: 0=major,1=minor,2=build,3=revision
            int idx = mode switch
            {
                "major" => 0,
                "minor" => 1,
                "build" => 2,
                _ => 3,
            };

            // increment selected part
            parts[idx]++;

            // when incrementing a higher part, reset all less significant parts to 0
            for (int j = idx + 1; j < 4; j++)
                parts[j] = 0;

            int[] max = new[] { options.MaxMajor, options.MaxMinor, options.MaxBuild, options.MaxRevision };

            // propagate overflow upward (to more significant parts)
            for (int i = idx; i >= 0; i--)
            {
                if (parts[i] > max[i])
                {
                    parts[i] = 0;
                    if (i - 1 >= 0)
                        parts[i - 1]++;
                }
            }

            // if major exceeded its max, clamp and zero lower parts
            if (parts[0] > max[0])
            {
                parts[0] = max[0];
                parts[1] = parts[2] = parts[3] = 0;
            }

            return $"{parts[0]}.{parts[1]}.{parts[2]}.{parts[3]}";
        }

        // ================================================================
        // Ignored patterns
        // ================================================================
        static List<string> LoadIgnoredPatterns(string path)
        {
            if (!File.Exists(path)) return new();

            if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                var json = File.ReadAllText(path);
                var list = JsonSerializer.Deserialize<List<string>>(json);
                return list?.Where(l => !string.IsNullOrWhiteSpace(l)).ToList() ?? new();
            }

            return File.ReadAllLines(path)
                .Select(l => l.Trim())
                .Where(l => !string.IsNullOrEmpty(l) && !l.StartsWith("#"))
                .ToList();
        }

        internal static bool IsIgnored(string filePath, List<string> ignored)
        {
            string fileName = Path.GetFileName(filePath);
            string fullPath = Path.GetFullPath(filePath);

            foreach (var pattern in ignored)
            {
                if (string.IsNullOrWhiteSpace(pattern)) continue;

                bool isRegex = pattern.StartsWith("/regex:") || pattern.StartsWith("^") || pattern.EndsWith("$");

                if (isRegex)
                {
                    string regexPattern = pattern.StartsWith("/regex:") ? pattern[7..] : pattern;
                    if (Regex.IsMatch(fullPath, regexPattern, RegexOptions.IgnoreCase) ||
                        Regex.IsMatch(fileName, regexPattern, RegexOptions.IgnoreCase))
                        return true;
                }
                else
                {
                    string regexFromWildcard = WildcardToRegex(pattern);
                    if (Regex.IsMatch(fileName, regexFromWildcard, RegexOptions.IgnoreCase) ||
                        Regex.IsMatch(fullPath, regexFromWildcard, RegexOptions.IgnoreCase))
                        return true;
                }
            }
            return false;
        }

        internal static string WildcardToRegex(string pattern)
        {
            var sb = new StringBuilder();
            sb.Append('^');
            foreach (char c in pattern)
            {
                switch (c)
                {
                    case '*': sb.Append(".*"); break;
                    case '?': sb.Append('.'); break;
                    default: sb.Append(Regex.Escape(c.ToString())); break;
                }
            }
            sb.Append('$');
            return sb.ToString();
        }

        static Options ParseArgs(string[] args)
        {
            var opts = new Options();

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                switch (arg)
                {
                    case "--help": case "-h": opts.ShowHelp = true; break;
                    case "--path": case "-p": if (i + 1 < args.Length) opts.Path = args[++i]; break;
                    case "--ext":
                    case "-e":
                        if (i + 1 < args.Length)
                        {
                            opts.Extensions = args[++i]
                                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                .Select(x => x.StartsWith('.') ? x : "." + x)
                                .ToList();
                        }
                        break;
                    case "--ignored": case "-i": if (i + 1 < args.Length) opts.IgnoredFile = args[++i]; break;
                    case "--report": case "-r": if (i + 1 < args.Length) opts.ReportPath = args[++i]; break;
                    case "--max-major": if (i + 1 < args.Length) opts.MaxMajor = int.Parse(args[++i]); break;
                    case "--max-minor": if (i + 1 < args.Length) opts.MaxMinor = int.Parse(args[++i]); break;
                    case "--max-build": if (i + 1 < args.Length) opts.MaxBuild = int.Parse(args[++i]); break;
                    case "--max-revision": if (i + 1 < args.Length) opts.MaxRevision = int.Parse(args[++i]); break;
                    case "--increment-mode":
                        if (i + 1 < args.Length)
                        {
                            var mode = args[++i].ToLower();
                            if (mode is "major" or "minor" or "build" or "revision")
                                opts.IncrementMode = mode;
                        }
                        break;
                }
            }
            return opts;
        }


        static void ShowHelp()
        {
            Console.WriteLine(@"
    VSU - VersionSolutionUpdater – automatic version updater for files

Description:
  Scans a folder (recursively) for files with specified extensions,
  and increments the version in lines starting with '// Version: x.x.x.x'
  only if the file content changed (excluding the version line).
  New files automatically receive '// Version: 1.0.0.0'.

CLI options:

  -p, --path <folder>
      Root folder to scan. Default: current directory.

  -e, --ext <list>
      Comma-separated list of file extensions to process, e.g. .cs,.txt,.json
      Default: .cs

  -i, --ignored <file>
      Path to ignored patterns file (ignored.txt or ignored.json).
      If not specified, the program searches for 'ignored.txt' or 'ignored.json' in the root folder.

  -r, --report <file>
      Path to report file listing modified files and new versions.
      Default: version_raport.txt

  --increment-mode <mode>
      Which part of the version to increment when a change is detected.
      Options:
        revision  (default) – increments x.x.x.REVISION
        build     – increments x.x.BUILD.x
        minor     – increments x.MINOR.x.x
        major     – increments MAJOR.x.x.x

  --max-major <int>
  --max-minor <int>
  --max-build <int>
  --max-revision <int>
      Maximum values for each version component. Higher parts increment automatically when exceeded.
      Default: 99 for all

  -h, --help
      Show this help

Ignored file format (ignored.txt or ignored.json):

  - Lines starting with # are comments
  - Wildcards:
      *      – any sequence of characters
      ?      – one character
      [abc]  – one character from set
      [!abc] – one character NOT in set
  - Examples:
      *.txt          – all .txt files
      [Dd]*ing.txt   – Dubing.txt, dubing.txt, Doing.txt, etc.
  - Regex patterns (prefix /regex:):
      /regex:^Test.*\.cs$   – all files starting with 'Test' ending with .cs
      /regex:(temp|cache)   – all paths containing 'temp' or 'cache'
  - You can mix wildcards and regex in the same file

Examples:

  dotnet run -- --path ""C:\Project""
  dotnet run -- --path ""C:\Project"" --ext "".cs,.json"" --ignored ""C:\ignored.txt""
  dotnet run -- --path ""C:\Project"" --increment-mode build --max-revision 9 --max-build 99
  dotnet run -- --report ""C:\report.txt""

or run the compiled executable:
  vsu.exe --path ""C:\Project"" --ext "".cs,.json"" --ignored ""C:\ignored.txt""
");
        }

        internal class Options
        {
            public bool ShowHelp { get; set; }
            public string? Path { get; set; }
            public string? IgnoredFile { get; set; }
            public string? ReportPath { get; set; }
            public List<string> Extensions { get; set; } = new();
            public int MaxMajor { get; set; } = 99;
            public int MaxMinor { get; set; } = 99;
            public int MaxBuild { get; set; } = 99;
            public int MaxRevision { get; set; } = 99;
            public string IncrementMode { get; set; } = "revision";
        }
    }

    class VersionHistory
    {
        public string File { get; set; } = "";
        public string OldVersion { get; set; } = "";
        public string NewVersion { get; set; } = "";
        public DateTime Date { get; set; }
    }
}