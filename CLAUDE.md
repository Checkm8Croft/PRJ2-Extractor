# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Development Commands

### Building the Solution
The solution uses the .NET SDK format and can be built with:
- `dotnet build` - builds both PRJ2 Extractor (WPF) and PrjDiag (console) projects
- `msbuild PRJ2 Extractor.slnx` - alternative using MSBuild

The GitHub Actions workflow (`.github/workflows/dotnet-desktop.yml`) builds the solution in both Debug and Release configurations on Windows.

### Running the Applications
- **PRJ2 Extractor (GUI)**: Run `PRJ2 Extractor\bin\Debug\net10.0-windows\PRJ2 Extractor.exe` (or the Release equivalent)
- **PrjDiag (diagnostic console)**: Run `PrjDiag\bin\Debug\net10.0-windows\PrjDiag.exe` - this tool exports a TR4 file to PRJ2 and validates the output using TombLib's loader

### Testing
There are no test projects in the repository. The GitHub workflow runs `dotnet test` but it will not execute any tests.

## Project Structure

### PRJ2 Extractor (Main WPF Application)
- `App.xaml/App.xaml.cs` - WPF application entry point
- `MainWindow.xaml/MainWindow.xaml.cs` - Main GUI for loading TR4 files, converting to PRJ, and exporting to PRJ2
- `Core/` - Core logic for TR4 parsing and conversion
  - `TrLevel.cs` - Parses Tomb Raider 4 (.tr4) level files and provides conversion methods
  - `TrProject.cs` - Intermediate representation of a classic Tomb Raider level project (PRJ format)
  - `Prj2Exporter.cs` - Exports TrLevel to Tomb Editor PRJ2 format using TombLib.dll
  - `TgaWriter.cs` - Utility for saving textures as TGA files
- `Models/` - Data structures representing TR4 file format structures
  - `TrLevelModels.cs` - Enums and classes for sectors, rooms, portals, textures, etc.
  - `TrProjectModels.cs` - Classic PRJ format structures (blocks, doors, rooms)

### PrjDiag (Diagnostic Tool)
- `Program.cs` - Console application that tests the PRJ2 export functionality by:
  1. Loading a TR4 file
  2. Exporting to PRJ2 using Prj2Exporter
  3. Reloading the PRJ2 through TombLib's loader
  4. Checking for invalid slopes using TombLib's validation

### Dependencies
The project relies on several native DLLs located at `C:\Tomb Editor\` (reference paths are hardcoded in the project files). These include:
- TombLib.dll (core library for PRJ2 reading/writing)
- AssimpNet.dll (3D model import)
- NAudio.dll (audio processing)
- Various image and compression libraries (DevIL, FreeImage, NLog, etc.)

These dependencies must be present at the specified path for the project to build and run correctly.

## Architecture Overview

1. **TR4 Parsing** (`TrLevel.cs`):
   - Reads and decompresses TR4 level files
   - Extracts geometry, textures, rooms, portals, and sector floor/ceiling data
   - Provides `ConvertToPrj()` method to convert to classic PRJ format

2. **Intermediate Representation** (`TrProject.cs`):
   - Models the classic Tomb Raider level project (PRJ) format
   - Contains rooms, blocks, doors, and texture information
   - Used as a stepping stone between TR4 and PRJ2 formats

3. **PRJ2 Export** (`Prj2Exporter.cs`):
   - Takes a TrLevel and converts it to a TrProject (with fixFdivs=false to avoid invalid geometry)
   - Applies door processing via `MakeDoors()`
   - Uses TombLib to create an in-memory Level/Room object graph from the TrProject data
   - Writes the PRJ2 file using TombLib's `Prj2Writer`

4. **GUI Interaction** (`MainWindow.xaml.cs`):
   - Allows users to load TR4 files and optionally load a PRJ for reference
   - Provides controls to copy doors/textures/lights from a reference PRJ
   - Exports to PRJ2 format with progress reporting and error handling

## Key Implementation Details

- The TR4 parser handles both compressed and uncompressed sections of the file format
- Floor data includes slope information, triggers, doors, and special floor/ceiling types
- Door detection in TR4 portals is complex - multiple adjacent portals may represent a single door
- The PRJ2 export process involves two passes:
  1. Creating room geometry from block data (including slope information)
  2. Adding alternate (flip) room links and portals derived from door information
- TombLib handles the actual PRJ2 file writing, ensuring compliance with the Tomb Editor format

## Important Notes
- The hardcoded dependency path `C:\Tomb Editor\` must be configured for successful builds
- The application is specifically designed for Tomb Raider 4 (TR4) level files
- PRJ2 output is compatible with Tomb Editor (a modern Tomb Raider level editor)