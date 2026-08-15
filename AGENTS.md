# Repository instructions

## Working tree safety

- Assume the working tree may contain user changes. Preserve unrelated edits and
  do not revert, reset, or overwrite them.
- Keep changes narrowly scoped to the requested work.

## Build and verification

- After C# changes, build the x64 Release configuration:

  `dotnet build .\IstripperQuickPlayer\IStripperQuickPlayer.csproj -c Release -p:Platform=x64 --no-restore`

- For custom-show application logic, run the built Release executable with
  `--verify-custom-shows` and require exit code 0.
- After changing a Python worker under `tools\custom-shows`, run that worker with
  `--self-test` using the configured custom-show virtual-environment Python.
- Run `git diff --check` before handing off code changes.

## Rebuild, publish, and relaunch

When asked to rebuild, publish, or relaunch QuickPlayer, publish a
framework-independent (self-contained) `win-x64` Release application directly
into this directory:

`C:\Users\Jeremy\source\repos\KittyPingu\IStripperQuickPlayer\IstripperQuickPlayer\bin\x64\Release\net10.0-windows`

Use this command from the repository root:

`dotnet publish .\IstripperQuickPlayer\IStripperQuickPlayer.csproj -c Release -p:Platform=x64 -r win-x64 --self-contained true -o .\IstripperQuickPlayer\bin\x64\Release\net10.0-windows`

Before publishing, stop only a running process whose executable path exactly
matches the target below. After publishing, verify that the file exists and
launch this exact executable:

`C:\Users\Jeremy\source\repos\KittyPingu\IStripperQuickPlayer\IstripperQuickPlayer\bin\x64\Release\net10.0-windows\IstripperQuickPlayer.exe`

Do not stop, launch, or publish over the Debug output, an installed copy, or any
other QuickPlayer executable.
