# DS3CollisionExport

Standalone C# tools for Dark Souls 3 collision HKX export.

## Notes

- This project's code references ideas and implementation patterns from Smithbox.
- The code in this project was generated with AI assistance.

## Tools

- `src/hkx-exporter`: export DS3 HKX collision to OBJ/JSON, with optional MSB collision-part instance transform.
- `src/hkx-reflect`: reflection helper for inspecting HKX2 types/fields.
- `src/navmesh-exporter`: export DS3 `*.nvmhktbnd.dcx` navmesh HKX to OBJ/JSON.

## Prerequisites

- .NET SDK 5.0+ (`dotnet --info`)
- local dependency DLLs in `vendor/` (not tracked by Git):
  - `vendor/HKX2.dll`
  - `vendor/SoulsFormats.dll`

## Dependency Source and Version

Both DLLs should be copied from `SoulsCollisionExport_1_2_7`:

- `HKX2.dll`
  - source: `SoulsCollisionExport_1_2_7`
  - file size: `878,080 bytes`
- `SoulsFormats.dll`
  - source: `SoulsCollisionExport_1_2_7`
  - file size: `911,872 bytes`

## Quick Start (PowerShell)

```powershell
dotnet build .\src\hkx-exporter\hkx-exporter.csproj
dotnet build .\src\hkx-reflect\hkx-reflect.csproj
dotnet build .\src\navmesh-exporter\navmesh-exporter.csproj
```

## hkx-exporter Usage

```powershell
cd .\src\hkx-exporter
dotnet run -- --hkx-dir "..\..\input\m40_00_00_00\collision_export_h40\binder_unpack\m40_00_00_00" --msb "..\..\input\mapstudio\m40_00_00_00.msb.dcx" --out-obj-dir "..\..\output\collision_h40_world_objs" --out-json "..\..\output\collision_h40_world.json"
```

### Arguments

- `--hkx-dir`: folder containing unpacked `*.hkx`
- `--msb`: DS3 `*.msb.dcx` path (used for `Part.Collision` model instance transforms)
- `--out-obj-dir`: output folder for OBJ files
- `--out-json`: output JSON sidecar path

## hkx-reflect Usage

```powershell
cd .\src\hkx-reflect
dotnet run -- "<path-to-some.hkx>"
```

## navmesh-exporter Usage

```powershell
cd .\src\navmesh-exporter
dotnet run -- --nvmhktbnd "D:\path\to\m40_00_00_00.nvmhktbnd.dcx" --out-obj-dir "..\..\output\navmesh_objs" --out-json "..\..\output\navmesh_summary.json"
```

### Arguments

- `--nvmhktbnd`: DS3 `*.nvmhktbnd.dcx` path
- `--out-obj-dir`: output folder for per-navmesh OBJ files
- `--out-json`: output JSON summary path
