# 🧩 VSU – Version Solution Updater

**VSU** (Version Solution Updater) is a lightweight CLI tool written in **C#**, designed to automatically update version numbers in source files (e.g., `.cs`, `.js`, `.c`, `.cpp` etc.) **only when the file’s content changes**.  
It also maintains version history, unified hash tracking, and generates statistical reports for project version management.

---

## 📦 Installation

🔧 Prerequisites

- NET 8.0 SDK or later installed.

🔨 Building from Source

1. Clone the repository:

```bash
git clone https://github.com/softbery-org/vsu.git
cd vsu
```

2.Restore dependencies:

```bash
dotnet restore
```

3.Publish the tool (self-contained executable):

```bash
dotnet publish -c Release -r <runtime> --self-contained true -o out
```

Replace `runtime` with your target runtime (e.g., win-x64 for Windows, linux-x64 for Linux).

4.The VSU executable will be available in the out directory. Add it to your PATH for global use.

## 📥 Binary Releases

Pre-built binaries are available on the [GitHub Releases page](https://github.com/softbery-org/vsu "VSU project repository"). Download the appropriate archive for your platform, extract, and add the executable to your PATH.

---

## 🚀 Features

- 🧠 **Automatic version detection** — recognizes `// Version: x.x.x.x` lines.
- 🧩 **Content-based versioning** — versions are incremented only if the file’s content (excluding the version line) changes.
- 📦 **Unified hash tracking** — all file hashes are stored in one `hashes.json` file.
- 📊 **Project statistics** — calculates an average project version across all scanned files.
- 🕓 **Version history** — keeps detailed change records in `version_history.json`.
- ⚙️ **Configurable via CLI** — supports custom extensions, paths, version increment mode, and ignored patterns.

---

## 🧰 Command-Line Options

| Option              | Alias | Description |
|---------------------|-------|-------------|
| `--path <folder>`   | `-p`  | Root folder to scan. Default: current directory. |
| `--ext <list>`      | `-e`  | Comma-separated list of file extensions to process, e.g. `.cs,.txt,.json`. Default: `.cs`. |
| `--ignored <file>`  | `-i`  | Path to ignored patterns file (`ignored.txt` or `ignored.json`). |
| `--report <file>`   | `-r`  | Path to report file. Default: `version_raport.txt`. |
| `--increment-mode <mode>` |    | Which part of version to increment: `major`, `minor`, `build`, or `revision` (default). |
| `--max-major <int>` |       | Maximum MAJOR version value (default: 99). |
| `--max-minor <int>` |       | Maximum MINOR version value (default: 99). |
| `--max-build <int>` |       | Maximum BUILD version value (default: 99). |
| `--max-revision <int>` |    | Maximum REVISION version value (default: 99). |
| `--help`            | `-h`  | Show help and usage instructions. |

---

### 🗂️ Ignored File Format (`ignored.txt` / `ignored.json`)

Ignored files and directories can be defined with:

- Wildcards (`*`, `?`, `[abc]`, `[!abc]`)
- Regex (with `/regex:` prefix or `^...$` patterns)
- Comments (lines starting with `#`)

**Examples**
**`ignored.txt`**

```txt
# Ignore all text files
*.txt

# Ignore files like Dubing.txt, Doing.txt
[Dd]*ing.txt

# Ignore temp or cache folders
/regex:(temp|cache)
```

**`ignored.json`**

```json
[
  "*.log",
  "/regex:^Test.*\\.cs$",
  "[Dd]*ing.txt"
]
```

---

## 📄 Version Format

Each tracked file starts with or contains a version line:

```csharp
// Version: 1.0.0.0
```

New files automatically start with 1.0.0.0.
The program only increments version numbers if the file’s content (excluding this line) changes.
You can specify which part of the version to increment (--increment-mode).

---

## 🧮 Version Statistics

After scanning all files, VSU computes the average version based on all version numbers across the project.
This value represents the overall maturity or progress of your project versioning.
Example output in `version_raport.txt`:

```txt
=== Version Statistics ===
Total files processed: 42
Files updated: 5
Average project version: 1.3.7.0
```

---

## 🪣 Hash Storage (`hashes.json`)

All file hashes (used to detect content changes) are stored in one file:

```json
{
  "C:\\Project\\Main.cs": "A7B19F3E2E...",
  "C:\\Project\\Data\\Config.json": "B35D10AA74..."
}
```

If a file’s hash matches the stored one, its version will not be incremented.

---

## 🕓 Version History `(version_history.json`)

Each time a file version changes, an entry is appended to `version_history.json`.
Example:

```json
[
  {
    "File": "C:\\Project\\Main.cs",
    "OldVersion": "1.0.0.3",
    "NewVersion": "1.0.0.4",
    "Date": "2025-10-23T18:00:00Z"
  },
  {
    "File": "C:\\Project\\Services\\ConfigHandler.cs",
    "OldVersion": "1.2.0.0",
    "NewVersion": "1.2.1.0",
    "Date": "2025-10-23T18:00:00Z"
  }
]
```

This provides a complete historical log of version evolution across your project.

---

## 🧾 Example Console Output

```bash
[INFO] Starting in: C:\Projects\VSU
[INFO] Ignored patterns: 3
[INFO] Extensions: .cs
[INFO] Increment mode: revision

[=] C:\Projects\VSU\Utils.cs – no changes
[↑] C:\Projects\VSU\Program.cs – new version 1.0.1.0
[-] Ignoring: C:\Projects\VSU\temp\debug.txt

[OK] Finished. Report: C:\Projects\VSU\version_raport.txt
[INFO] Average project version: 1.2.4.0
```

## 📊 Output Files Summary

| File                           | Description                                            |
| ------------------------------ | ------------------------------------------------------ |
| `version_raport.txt`           | List of updated files and statistical summary.         |
| `hashes.json`                  | Unified storage of SHA256 hashes for change detection. |
| `version_history.json`         | Log of all version changes with timestamps.            |
| `ignored.txt` / `ignored.json` | List of ignored files and directories.                 |

---

## 💻 Usage Examples

```bash
# Scan current directory
VSU

# Scan specific folder
VSU --path "C:\Projects\VSU"

# Specify extensions
VSU --ext ".cs,.json"

# Use a custom ignored file
VSU --ignored "C:\Configs\ignored.json"

# Increment build number instead of revision
VSU --increment-mode build

# Set revision limit to 9 and build limit to 99
VSU --max-revision 9 --max-build 99
```

## Usage in Visual Studio

# add in project file `project.csproj`

```xml
  <Target Name="UpdateAssemblyVersion" BeforeTargets="BeforeBuild">
    <Exec Command="[path to vsu.exe file]" />
  </Target>

```

---

## 🧠 Notes

- Versions follow the format: MAJOR.MINOR.BUILD.REVISION
- Version line must start with // Version: (case-insensitive)
- Hashes ensure integrity across multiple runs
- The average version is recalculated every run
- Works recursively in all subfolders

## 🧩 Example Folder Structure

```txt
📁 ProjectRoot
 ┣ 📄 ignored.txt
 ┣ 📄 version_raport.txt
 ┣ 📄 hashes.json
 ┣ 📄 version_history.json
 ┣ 📂 Source
 ┃ ┣ 📄 Program.cs
 ┃ ┗ 📄 Utils.cs
 ┗ 📂 temp
    ┗ 📄 test.txt
```

---

## 🔗 Ascii diagram created with [asciiflow](http://asciiflow.com/)

```plaintext
                              +----------------------+
                              |     Start Program    |
                              +----------------------+
                                         |
                                         v
                              +----------------------+
                              |   Parse CLI args     |
                              +----------------------+
                                         |
                                         v
                      +-------------------------------------+
                      | Determine root & ignored file paths |
                      +-------------------------------------+
                                         |
                                         v
                        +-------------------------------+
                        | Load hashes.json (or create)  |
                        +-------------------------------+
                                         |
                                         v
                        +-------------------------------+
                        | Load version_history.json     |
                        | (or create empty list)        |
                        +-------------------------------+
                                         |
                                         v
                        +-------------------------------+
                        | Load ignored patterns file    |
                        +-------------------------------+
                                         |
                                         v
                        +-------------------------------+
                        | Build allowed extension set   |
                        +-------------------------------+
                                         |
                                         v
                        +-------------------------------+
                        | Scan folder (recursive)       |
                        +-------------------------------+
                                         |
                                         v
                        +-------------------------------+
                        | For each file with allowed ext|
                        +-------------------------------+
                                         |
                          +--------------+--------------+
                          |                             |
                          v                             v
               +------------------------+     +------------------------+
               | If file matches ignored|     | Read file lines        |
               |    -> skip file        |     +------------------------+
               +------------------------+                |
                                                            v
                                               +-------------------------------+
                                               | Find "// Version: x.x.x.x"    |
                                               +-------------------------------+
                                                            |
                                         +------------------+------------------+
                                         |                                     |
                                         v                                     v
                             [version line exists]                   [no version line -> will insert]
                                         |                                     |
                                         v                                     v
                           +---------------------------------+     +----------------------------+
                           | Compute content-without-version |     | // Version inserted at top |
                           | hash                            |     +----------------------------+
                           +---------------------------------+     
                                         |
                                         v
                        +---------------------------------------------+
                        | Compare newHash with hashes.json entry      |
                        +---------------------------------------------+
                                         |
                      +------------------+-------------------+
                      |                                      |
                      v                                      v
             [hash equal && had version]               [hash differ OR new file]
                      |                                      |
                      v                                      v
             +--------------------+             +-------------------------------+
             | No change -> skip  |             | Increment version (mode)      |
             +--------------------+             +-------------------------------+
                                                      |
                                                      v
                                         +-------------------------------+
                                         | Update version line in file   |
                                         | Write file                    |
                                         | Update hashes dictionary      |
                                         +-------------------------------+
                                                      |
                                                      v
                                         +--------------------------------+
                                         | Append entry to version history|
                                         | (file, oldVersion, newVersion, |
                                         |  timestamp UTC)                |
                                         +--------------------------------+
                                                      |
                                                      v
                                         +-------------------------------+
                                         | Add entry to report list      |
                                         +-------------------------------+
                                                      |
                                                      v
                                         +-------------------------------+
                                         | Next file                     |
                                         +-------------------------------+
                                                      |
                                                      v
                                         +--------------------------------+
                                         | After all files:               |
                                         | - Save hashes.json             |
                                         | - Save version_history.json    |
                                         | - Compute average project ver. |
                                         | - Write version_raport.txt     |
                                         +--------------------------------+
                                                      |
                                                      v
                                         +-------------------------------+
                                         |            End                |
                                         +-------------------------------+

```

---

## 📚 License

MIT License © 2025
VSU (Version Solution Updater) – a modern, automated version management tool for C# projects and beyond.
Created for help with automation, and traceability in versioning.

2025 &copy; Softbery by Paweł Tobis
