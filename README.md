# <img src="Workspace/Assets/logo.png" alt="GitWorkspace logo" width="32" /> GitWorkspace

A native Windows Git GUI with AvalonEdit document tabs, side-by-side diffs, a
commit graph, `git grep` search, ConPTY + xterm.js terminal, and optional
Dart / Go / Python / TypeScript / Rust / C# / PHP / C/C++ language servers.
Built with WPF (.NET 10) — no Electron.

## Requirements

- Windows 10 1809+ or Windows 11
- .NET 10 Desktop Runtime (SDK for building)
- Git for Windows
- Edge WebView2 Runtime
- Node/npm only for the first build (restores the two xterm packages)

Optional for language features:

- Flutter / Dart SDK when the opened repo has `pubspec.yaml`
- `go` + `gopls` when the opened repo has `go.mod` (root or `go/go.mod`)
- Python language server when the opened repo has `pyproject.toml`,
  `requirements.txt`, `setup.py`, `setup.cfg`, or `Pipfile` — prefers
  `basedpyright-langserver`, then `pyright-langserver`, then `pylsp` on PATH
  (also searches common npm / Python install dirs)
- Optional `ruff` on PATH for Python **CLI snapshot**
- `typescript-language-server` when the opened repo has root `package.json`,
  `tsconfig.json`, or `jsconfig.json` (npm global / PATH); optional `tsc` for
  TypeScript **CLI snapshot**. Pin TypeScript to 5.x for the language server —
  TypeScript 7 removed `tsserver.js`, which `typescript-language-server` still
  needs (`npm i -g typescript-language-server typescript@5`)
- `rust-analyzer` when the opened repo has `Cargo.toml` (PATH or `~/.cargo/bin`);
  optional `cargo check` for Rust **CLI snapshot**. Install the analyzer
  component (`rustup component add rust-analyzer`), not only the rustup shim
- `csharp-ls` (or OmniSharp `-lsp`) when a `.sln` / `.csproj` exists at the
  root or one project folder deep; optional `dotnet build` for C# **CLI snapshot**.
  On .NET 10, pin `csharp-ls` 0.27.0 (`dotnet tool install -g csharp-ls --version 0.27.0`);
  newer package versions may require .NET 11
- `intelephense` (or `phpactor language-server`) when the opened repo has
  `composer.json`, `phpunit.xml`, or `phpunit.xml.dist`; optional `phpstan`
  (preferred) or `php -l` for PHP **CLI snapshot**
- `clangd` when the opened repo has `compile_commands.json`, `CMakeLists.txt`,
  `Makefile`/`makefile`, `meson.build`, or `.clangd` (also checks
  `build/compile_commands.json`); optional `clang-tidy` + compile database for
  C/C++ **CLI snapshot**

## Start

```powershell
./Run-GitGui.cmd
# or
./GitGui.ps1
./GitGui.ps1 -Repository D:\path\to\checkout
```

The launcher builds when the executable is missing or sources changed, then
starts `Workspace/bin/Release/net10.0-windows/GitWorkspace.exe`.

### Opening a repository

Resolution order:

1. `--repository <path>` (quotes around the path are stripped)
2. Most recent valid entry in `%LOCALAPPDATA%/GitWorkspace/recent-repos.json`
3. Walk upward from the executable for a `.git` directory
4. Open Folder dialog (cancel exits)

Use **File → Open Repository…** to switch checkouts while running.

## Workflow

- **Explorer** — browse files (including untracked/ignored), edit text in the
  File tab (Ctrl+S / Ctrl+Shift+R), view staged and working-tree diffs.
- **Search** — repository-wide `git grep` with case / word / regex and
  include/exclude globs (Ctrl+Shift+F).
- **Source Control** — stage, commit, pull, push, discard, file history, and
  commit graph with side-by-side revision diffs.
- **Problems** — language servers start only when matching project files exist
  (Dart, gopls, Python LSP, typescript-language-server, rust-analyzer,
  csharp-ls/OmniSharp, intelephense/phpactor, clangd). **CLI snapshot** runs
  `flutter analyze` (or `dart analyze`), `go vet`, `ruff check`, `tsc --noEmit`,
  `cargo check`, `dotnet build`, `phpstan`/`php -l`, and/or `clang-tidy` for
  detected stacks.
- **Terminal** — interactive PowerShell via ConPTY; WebView2 + xterm.js loads
  on first open.

Git lock recovery backups live under
`%LOCALAPPDATA%/GitWorkspace/recovered-git-locks/`. Window layout and recent
repos are stored under `%LOCALAPPDATA%/GitWorkspace/`.

## Build and test

```powershell
./Build-Workspace.ps1
./Test-Workspace.ps1
```

Or:

```powershell
dotnet build Workspace/Workspace.csproj -c Release
dotnet run --project SmokeTests/SmokeTests.csproj -c Release
```

Against a multi-stack checkout:

```powershell
dotnet run --project SmokeTests/SmokeTests.csproj -c Release -- --analyzers D:\path\to\repo
```

## Releases

Tagged versions (`v*`) build a Windows x64 zip via GitHub Actions and attach it
to the GitHub Release as `GitWorkspace-win-x64.zip`.
