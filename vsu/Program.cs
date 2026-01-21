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
        private static readonly Regex VERSION_REGEX = new(@"^\d+\.\d+\.\d+\.\d+$", RegexOptions.Compiled);

        static void Main(string[] args)
        {
            var options = ParseArgs(args);
            if (options.ShowHelp)
            {
                ShowHelp();
                return;
            }

            string rootFolder = options.Path ?? Directory.GetCurrentDirectory();
            if (!Directory.Exists(rootFolder))
            {
                Console.Error.WriteLine($"[ERROR] Path does not exist: {rootFolder}");
                Environment.Exit(1);
            }

            try
            {
                ValidateOptions(options, rootFolder);
            }
            catch (ArgumentException ex)
            {
                Console.Error.WriteLine($"[ERROR] Invalid argument: {ex.Message}");
                ShowHelp();
                Environment.Exit(1);
            }

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
            if (!string.IsNullOrEmpty(options.SetAllVersion))
                Console.WriteLine($"[INFO] Manual all version: {options.SetAllVersion}");
            if (options.SetFiles.Count > 0)
                Console.WriteLine($"[INFO] Manual files: {string.Join(", ", options.SetFiles)} to {options.SetVersion ?? "none"}");
            bool hasPartialSets = options.SetMajor.HasValue || options.SetMinor.HasValue || options.SetBuild.HasValue || options.SetRevision.HasValue;
            if (hasPartialSets)
            {
                var partialInfo = new List<string>();
                if (options.SetMajor.HasValue) partialInfo.Add($"major={options.SetMajor}");
                if (options.SetMinor.HasValue) partialInfo.Add($"minor={options.SetMinor}");
                if (options.SetBuild.HasValue) partialInfo.Add($"build={options.SetBuild}");
                if (options.SetRevision.HasValue) partialInfo.Add($"revision={options.SetRevision}");
                Console.WriteLine($"[INFO] Manual partial: {string.Join(", ", partialInfo)}");
            }
            Console.WriteLine();

            foreach (var file in Directory.EnumerateFiles(rootFolder, "*.*", SearchOption.AllDirectories)
                         .Where(f => allowedExtensions.Contains(Path.GetExtension(f))))
            {
                if (IsIgnored(file, ignoredPatterns))
                {
                    Console.WriteLine($"[-] Ignoring: {file}");
                    continue;
                }

                bool isTarget = options.SetFiles.Count == 0;
                if (!isTarget)
                {
                    isTarget = options.SetFiles.Any(sf =>
                    {
                        try
                        {
                            string fullSetPath = Path.GetFullPath(Path.Combine(rootFolder, sf.Trim()));
                            return file.Equals(fullSetPath, StringComparison.OrdinalIgnoreCase);
                        }
                        catch
                        {
                            return false;
                        }
                    });
                }

                string? manualFull = null;
                if (isTarget)
                {
                    if (options.SetFiles.Count == 0)
                    {
                        manualFull = options.SetAllVersion;
                    }
                    else
                    {
                        manualFull = options.SetVersion;
                    }
                }

                string? targetVersionStr = null;
                bool isManual = false;
                if (isTarget)
                {
                    if (!string.IsNullOrEmpty(manualFull))
                    {
                        // Parse and clamp full version
                        var parts = ParseVersionToParts(manualFull);
                        int[] maxs = { options.MaxMajor, options.MaxMinor, options.MaxBuild, options.MaxRevision };
                        for (int i = 0; i < 4; i++)
                        {
                            if (parts[i] > maxs[i]) parts[i] = maxs[i];
                        }
                        targetVersionStr = string.Join(".", parts);
                        isManual = true;
                    }
                    else if (hasPartialSets)
                    {
                        // Partial set and clamp only set parts
                        string currentVer = GetVersionFromFile(file) ?? "0.0.0.0";
                        var parts = ParseVersionToParts(currentVer);
                        if (options.SetMajor.HasValue)
                        {
                            parts[0] = Math.Min(options.SetMajor.Value, options.MaxMajor);
                        }
                        if (options.SetMinor.HasValue)
                        {
                            parts[1] = Math.Min(options.SetMinor.Value, options.MaxMinor);
                        }
                        if (options.SetBuild.HasValue)
                        {
                            parts[2] = Math.Min(options.SetBuild.Value, options.MaxBuild);
                        }
                        if (options.SetRevision.HasValue)
                        {
                            parts[3] = Math.Min(options.SetRevision.Value, options.MaxRevision);
                        }
                        targetVersionStr = string.Join(".", parts);
                        isManual = true;
                    }
                }

                try
                {
                    string versionBefore = GetVersionFromFile(file);
                    string? newVersion = ProcessFile(file, options, hashes, isManual, targetVersionStr);

                    if (newVersion != null)
                    {
                        updatedCount++;
                        report.Add($"{file} -> {newVersion}");
                        allVersions.Add(newVersion);

                        string histType = isManual ? "manual" : "auto";
                        history.Add(new VersionHistory
                        {
                            File = file,
                            OldVersion = string.IsNullOrEmpty(versionBefore) ? "none" : versionBefore,
                            NewVersion = newVersion,
                            Date = DateTime.UtcNow,
                            Type = histType
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

        private static void ValidateOptions(Options options, string rootFolder)
        {
            bool hasPartialSets = options.SetMajor.HasValue || options.SetMinor.HasValue || options.SetBuild.HasValue || options.SetRevision.HasValue;

            // Validate extensions
            if (options.Extensions.Any())
            {
                foreach (var ext in options.Extensions)
                {
                    if (string.IsNullOrWhiteSpace(ext))
                        throw new ArgumentException("Extension cannot be empty.");
                    if (!ext.StartsWith("."))
                        throw new ArgumentException($"Extension '{ext}' must start with a dot (.).");
                }
            }

            // Validate version formats
            if (!string.IsNullOrEmpty(options.SetAllVersion) && !VERSION_REGEX.IsMatch(options.SetAllVersion))
                throw new ArgumentException($"Invalid version format for --set-all-version: '{options.SetAllVersion}'. Expected: MAJOR.MINOR.BUILD.REVISION (e.g., 1.0.0.0)");

            if (!string.IsNullOrEmpty(options.SetVersion) && !VERSION_REGEX.IsMatch(options.SetVersion))
                throw new ArgumentException($"Invalid version format for --version: '{options.SetVersion}'. Expected: MAJOR.MINOR.BUILD.REVISION (e.g., 1.0.0.0)");

            // Validate max values (range: 0 to 999, for example; adjust as needed)
            if (options.MaxMajor < 0 || options.MaxMajor > 999)
                throw new ArgumentException("--max-major must be between 0 and 999");
            if (options.MaxMinor < 0 || options.MaxMinor > 999)
                throw new ArgumentException("--max-minor must be between 0 and 999");
            if (options.MaxBuild < 0 || options.MaxBuild > 999)
                throw new ArgumentException("--max-build must be between 0 and 999");
            if (options.MaxRevision < 0 || options.MaxRevision > 999)
                throw new ArgumentException("--max-revision must be between 0 and 999");

            // Validate set values (range: 0 to max, inclusive)
            if (options.SetMajor.HasValue && (options.SetMajor.Value < 0 || options.SetMajor.Value > options.MaxMajor))
                throw new ArgumentException($"--set-major {options.SetMajor} must be between 0 and {options.MaxMajor}");
            if (options.SetMinor.HasValue && (options.SetMinor.Value < 0 || options.SetMinor.Value > options.MaxMinor))
                throw new ArgumentException($"--set-minor {options.SetMinor} must be between 0 and {options.MaxMinor}");
            if (options.SetBuild.HasValue && (options.SetBuild.Value < 0 || options.SetBuild.Value > options.MaxBuild))
                throw new ArgumentException($"--set-build {options.SetBuild} must be between 0 and {options.MaxBuild}");
            if (options.SetRevision.HasValue && (options.SetRevision.Value < 0 || options.SetRevision.Value > options.MaxRevision))
                throw new ArgumentException($"--set-revision {options.SetRevision} must be between 0 and {options.MaxRevision}");

            // For full versions, validate each part against max
            if (!string.IsNullOrEmpty(options.SetAllVersion))
            {
                var parts = ParseVersionToParts(options.SetAllVersion);
                if (parts[0] > options.MaxMajor)
                    throw new ArgumentException($"MAJOR in --set-all-version ({parts[0]}) exceeds --max-major ({options.MaxMajor})");
                if (parts[1] > options.MaxMinor)
                    throw new ArgumentException($"MINOR in --set-all-version ({parts[1]}) exceeds --max-minor ({options.MaxMinor})");
                if (parts[2] > options.MaxBuild)
                    throw new ArgumentException($"BUILD in --set-all-version ({parts[2]}) exceeds --max-build ({options.MaxBuild})");
                if (parts[3] > options.MaxRevision)
                    throw new ArgumentException($"REVISION in --set-all-version ({parts[3]}) exceeds --max-revision ({options.MaxRevision})");
            }

            if (!string.IsNullOrEmpty(options.SetVersion))
            {
                var parts = ParseVersionToParts(options.SetVersion);
                if (parts[0] > options.MaxMajor)
                    throw new ArgumentException($"MAJOR in --version ({parts[0]}) exceeds --max-major ({options.MaxMajor})");
                if (parts[1] > options.MaxMinor)
                    throw new ArgumentException($"MINOR in --version ({parts[1]}) exceeds --max-minor ({options.MaxMinor})");
                if (parts[2] > options.MaxBuild)
                    throw new ArgumentException($"BUILD in --version ({parts[2]}) exceeds --max-build ({options.MaxBuild})");
                if (parts[3] > options.MaxRevision)
                    throw new ArgumentException($"REVISION in --version ({parts[3]}) exceeds --max-revision ({options.MaxRevision})");
            }

            // Validate increment mode
            var validModes = new[] { "major", "minor", "build", "revision" };
            if (!validModes.Contains(options.IncrementMode.ToLower()))
                throw new ArgumentException($"Invalid --increment-mode: '{options.IncrementMode}'. Valid: {string.Join(", ", validModes)}");

            // Validate paths
            if (!string.IsNullOrEmpty(options.IgnoredFile) && !File.Exists(options.IgnoredFile))
                throw new ArgumentException($"Ignored file does not exist: {options.IgnoredFile}");

            string reportPath = options.ReportPath ?? Path.Combine(rootFolder, VERSION_RAPORT_TXT);
            string? reportDir = Path.GetDirectoryName(reportPath);
            if (!string.IsNullOrEmpty(reportDir) && !Directory.Exists(reportDir))
                throw new ArgumentException($"Report directory does not exist: {reportDir}");

            if (options.SetFiles.Any())
            {
                if (string.IsNullOrEmpty(options.SetVersion) && !hasPartialSets)
                    throw new ArgumentException("--set-files requires either --version or at least one of --set-major, --set-minor, --set-build, --set-revision");

                foreach (var setFile in options.SetFiles)
                {
                    if (string.IsNullOrWhiteSpace(setFile))
                        throw new ArgumentException("Set file path cannot be empty.");
                    string fullSetPath;
                    try
                    {
                        fullSetPath = Path.GetFullPath(Path.Combine(rootFolder, setFile.Trim()));
                    }
                    catch (ArgumentException)
                    {
                        throw new ArgumentException($"Invalid set file path: {setFile}");
                    }
                    if (!File.Exists(fullSetPath))
                        throw new ArgumentException($"Set file does not exist: {fullSetPath}");
                }
            }

            // Validate path if provided
            if (!string.IsNullOrEmpty(options.Path) && !Directory.Exists(options.Path))
                throw new ArgumentException($"Path does not exist: {options.Path}");

            // Validate report path if provided
            if (!string.IsNullOrEmpty(options.ReportPath))
            {
                string? reportFullDir = Path.GetDirectoryName(Path.GetFullPath(options.ReportPath));
                if (!Directory.Exists(reportFullDir))
                    throw new ArgumentException($"Report path directory does not exist: {reportFullDir}");
            }

            // Additional CLI logic validation
            if (!string.IsNullOrEmpty(options.SetAllVersion) && options.SetFiles.Any())
                throw new ArgumentException("--set-all-version cannot be used with --set-files");
        }

        internal static int[] ParseVersionToParts(string version)
        {
            var parts = version.Split('.')
                .Select(x => int.TryParse(x, out int n) ? n : 0)
                .Take(4)
                .ToArray();
            while (parts.Length < 4) parts = parts.Append(0).ToArray();
            return parts;
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
        static string? ProcessFile(string filePath, Options options, Dictionary<string, string> hashes, bool isManual, string? targetVersion = null)
        {
            if (!File.Exists(filePath)) return null;

            var lines = File.ReadAllLines(filePath).ToList();
            int versionIndex = lines.FindIndex(l => l.TrimStart().StartsWith("// Version", StringComparison.OrdinalIgnoreCase));

            string currentVersion = versionIndex >= 0 ? GetVersionFromFile(filePath) : "";

            string contentWithoutVersion = string.Join("\n", lines.Where((_, i) => i != versionIndex));
            string newHash = ComputeHash(contentWithoutVersion);

            bool shouldUpdate = isManual;

            if (!isManual)
            {
                hashes.TryGetValue(filePath, out string? oldHash);
                if (oldHash == newHash && !string.IsNullOrEmpty(currentVersion))
                {
                    Console.WriteLine($"[=] {filePath} – no changes");
                    return null;
                }
                shouldUpdate = true;
            }

            if (!shouldUpdate) return null;

            string targetVer;
            if (isManual && !string.IsNullOrEmpty(targetVersion))
            {
                targetVer = targetVersion;
                Console.WriteLine($"[M] {filePath} – manual version {targetVer}");
            }
            else
            {
                targetVer = IncrementVersion(currentVersion ?? "1.0.0.0", options, options.IncrementMode);
                Console.WriteLine($"[↑] {filePath} – new version {targetVer}");
            }

            string versionLine = $"// Version: {targetVer}";
            if (versionIndex >= 0)
                lines[versionIndex] = versionLine;
            else
                lines.Insert(0, versionLine);

            File.WriteAllLines(filePath, lines);
            hashes[filePath] = newHash;

            return targetVer;
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
                    case "--help":
                    case "-h":
                        opts.ShowHelp = true;
                        break;
                    case "--path":
                    case "-p":
                        if (i + 1 >= args.Length)
                            throw new ArgumentException($"{arg} requires a value.");
                        opts.Path = args[++i];
                        if (string.IsNullOrWhiteSpace(opts.Path))
                            throw new ArgumentException("Path cannot be empty.");
                        break;
                    case "--ext":
                    case "-e":
                        if (i + 1 >= args.Length)
                            throw new ArgumentException($"{arg} requires a value.");
                        var extValue = args[++i];
                        if (string.IsNullOrWhiteSpace(extValue))
                            throw new ArgumentException("Extensions list cannot be empty.");
                        opts.Extensions = extValue
                            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                            .Where(x => !string.IsNullOrWhiteSpace(x))
                            .Select(x => x.StartsWith('.') ? x : "." + x)
                            .ToList();
                        if (!opts.Extensions.Any())
                            throw new ArgumentException("No valid extensions provided.");
                        break;
                    case "--ignored":
                    case "-i":
                        if (i + 1 >= args.Length)
                            throw new ArgumentException($"{arg} requires a value.");
                        opts.IgnoredFile = args[++i];
                        if (string.IsNullOrWhiteSpace(opts.IgnoredFile))
                            throw new ArgumentException("Ignored file path cannot be empty.");
                        break;
                    case "--report":
                    case "-r":
                        if (i + 1 >= args.Length)
                            throw new ArgumentException($"{arg} requires a value.");
                        opts.ReportPath = args[++i];
                        if (string.IsNullOrWhiteSpace(opts.ReportPath))
                            throw new ArgumentException("Report path cannot be empty.");
                        break;
                    case "--max-major":
                        if (i + 1 >= args.Length)
                            throw new ArgumentException($"{arg} requires a value.");
                        try
                        {
                            opts.MaxMajor = int.Parse(args[++i]);
                        }
                        catch (FormatException)
                        {
                            throw new ArgumentException($"Invalid integer for {arg}: {args[i]}");
                        }
                        break;
                    case "--max-minor":
                        if (i + 1 >= args.Length)
                            throw new ArgumentException($"{arg} requires a value.");
                        try
                        {
                            opts.MaxMinor = int.Parse(args[++i]);
                        }
                        catch (FormatException)
                        {
                            throw new ArgumentException($"Invalid integer for {arg}: {args[i]}");
                        }
                        break;
                    case "--max-build":
                        if (i + 1 >= args.Length)
                            throw new ArgumentException($"{arg} requires a value.");
                        try
                        {
                            opts.MaxBuild = int.Parse(args[++i]);
                        }
                        catch (FormatException)
                        {
                            throw new ArgumentException($"Invalid integer for {arg}: {args[i]}");
                        }
                        break;
                    case "--max-revision":
                        if (i + 1 >= args.Length)
                            throw new ArgumentException($"{arg} requires a value.");
                        try
                        {
                            opts.MaxRevision = int.Parse(args[++i]);
                        }
                        catch (FormatException)
                        {
                            throw new ArgumentException($"Invalid integer for {arg}: {args[i]}");
                        }
                        break;
                    case "--increment-mode":
                        if (i + 1 >= args.Length)
                            throw new ArgumentException($"{arg} requires a value.");
                        var modeValue = args[++i].ToLower();
                        if (string.IsNullOrWhiteSpace(modeValue))
                            throw new ArgumentException("Increment mode cannot be empty.");
                        if (modeValue is not "major" and not "minor" and not "build" and not "revision")
                            throw new ArgumentException($"Invalid increment mode: '{modeValue}'. Valid: major, minor, build, revision.");
                        opts.IncrementMode = modeValue;
                        break;
                    case "--set-all-version":
                    case "-s":
                        if (i + 1 >= args.Length)
                            throw new ArgumentException($"{arg} requires a value.");
                        opts.SetAllVersion = args[++i];
                        if (string.IsNullOrWhiteSpace(opts.SetAllVersion))
                            throw new ArgumentException("Set all version cannot be empty.");
                        break;
                    case "--set-files":
                    case "-f":
                        if (i + 1 >= args.Length)
                            throw new ArgumentException($"{arg} requires a value.");
                        var filesValue = args[++i];
                        if (string.IsNullOrWhiteSpace(filesValue))
                            throw new ArgumentException("Set files list cannot be empty.");
                        opts.SetFiles = filesValue
                            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                            .Where(x => !string.IsNullOrWhiteSpace(x))
                            .ToList();
                        if (!opts.SetFiles.Any())
                            throw new ArgumentException("No valid set files provided.");
                        break;
                    case "--version":
                        if (i + 1 >= args.Length)
                            throw new ArgumentException($"{arg} requires a value.");
                        opts.SetVersion = args[++i];
                        if (string.IsNullOrWhiteSpace(opts.SetVersion))
                            throw new ArgumentException("Set version cannot be empty.");
                        break;
                    case "--set-major":
                        if (i + 1 >= args.Length)
                            throw new ArgumentException($"{arg} requires a value.");
                        try
                        {
                            opts.SetMajor = int.Parse(args[++i]);
                        }
                        catch (FormatException)
                        {
                            throw new ArgumentException($"Invalid integer for {arg}: {args[i]}");
                        }
                        break;
                    case "--set-minor":
                        if (i + 1 >= args.Length)
                            throw new ArgumentException($"{arg} requires a value.");
                        try
                        {
                            opts.SetMinor = int.Parse(args[++i]);
                        }
                        catch (FormatException)
                        {
                            throw new ArgumentException($"Invalid integer for {arg}: {args[i]}");
                        }
                        break;
                    case "--set-build":
                        if (i + 1 >= args.Length)
                            throw new ArgumentException($"{arg} requires a value.");
                        try
                        {
                            opts.SetBuild = int.Parse(args[++i]);
                        }
                        catch (FormatException)
                        {
                            throw new ArgumentException($"Invalid integer for {arg}: {args[i]}");
                        }
                        break;
                    case "--set-revision":
                        if (i + 1 >= args.Length)
                            throw new ArgumentException($"{arg} requires a value.");
                        try
                        {
                            opts.SetRevision = int.Parse(args[++i]);
                        }
                        catch (FormatException)
                        {
                            throw new ArgumentException($"Invalid integer for {arg}: {args[i]}");
                        }
                        break;
                    default:
                        if (arg.StartsWith("-"))
                            throw new ArgumentException($"Unknown argument: {arg}");
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

  -s, --set-all-version <version>
      Manually set the exact version (e.g., 2.0.0.0) for ALL scanned files, ignoring content changes. Resets hashes.

  -f, --set-files <list> --version <version>
      Manually set the exact version for SPECIFIC files (comma-separated relative paths, e.g., Main.cs,Services/Utils.cs). Ignores content changes for these.

  --set-major <int>
  --set-minor <int>
  --set-build <int>
  --set-revision <int>
      Manually set a specific version component (MAJOR, MINOR, BUILD, REVISION) for all scanned files or specified files with --set-files.
      Other components remain unchanged. Values are clamped to max limits.

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
  dotnet run -- --set-all-version 2.0.0.0
  dotnet run -- --set-files ""Main.cs,Utils.cs"" --version 1.5.0.0
  dotnet run -- --set-major 3
  dotnet run -- --set-files ""Main.cs"" --set-revision 10
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
            public string? SetAllVersion { get; set; }
            public List<string> SetFiles { get; set; } = new();
            public string? SetVersion { get; set; }
            public int? SetMajor { get; set; }
            public int? SetMinor { get; set; }
            public int? SetBuild { get; set; }
            public int? SetRevision { get; set; }
        }
    }

    class VersionHistory
    {
        public string File { get; set; } = "";
        public string OldVersion { get; set; } = "";
        public string NewVersion { get; set; } = "";
        public DateTime Date { get; set; }
        public string Type { get; set; } = "auto";
    }
}