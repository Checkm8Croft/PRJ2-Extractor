# Repository Guidelines

## Project Structure & Module Organization
C#/.NET tool for extracting Tomb Raider PRJ2 files.
- `PRJ2 Extractor/` - Main app with `Core/` (extraction logic) and `Models/` (data models)
- `PrjDiag/` - Diagnostic tool
- Root: README, LICENSE, solution file

## Build, Test, and Development Commands
**Build:** Open solution in Visual Studio → Build Solution (`Ctrl+Shift+B`)
**Run:** Build and run PRJ2 Extractor project
**Test:** Currently manual via GUI; consider adding unit tests for `Prj2Exporter.cs`

## Coding Style & Naming Conventions
**Language:** C# .NET Framework
**Naming:** PascalCase for classes/methods, camelCase for fields/params, `_` prefix for private fields
**Formatting:** 4-space indents, Allman braces, System-using-first ordering
**Tools:** Visual Studio formatter, standard .NET conventions

## Testing Guidelines
Manual testing via GUI is primary. For future tests:
- Add test project (e.g., `PRJ2 Extractor.Tests`)
- Use xUnit/NUnit/MSTest for core logic in `Prj2Exporter.cs`
- Test naming: `[Method]_[Scenario]_[ExpectedResult]`

## Commit & Pull Request Guidelines
**Commits:** Imperative mood ("Add feature"), <50 char subject, reference issues
**PRs:** Describe changes, reference issues, add UI screenshots, ensure build success, test manually, keep focused

## Architecture Overview
**Main Components:**
1. **PRJ2 Extractor:** GUI + Core extraction logic (`TrProject.cs`, `TrLevel.cs`, `TgaWriter.cs`) + Models
2. **PrjDiag:** Diagnostic tool sharing similar structure

**Flow:** GUI → File parsing (`TrProject`/`TrLevel`) → Geometry extraction → Output (TGA) → Display/Save

**Key Tech:** .NET Framework 4.x, binary file I/O, TGA image generation, Windows desktop app

## Agent-Specific Instructions
**Code:** Follow existing conventions; core logic in `PRJ2 Extractor/Core/`; models in `Models/`; update both projects if shared logic affected
**Tests:** Manual GUI testing primary; consider unit tests for parsing
**Docs:** Update README for major changes; add XML comments to new public methods
