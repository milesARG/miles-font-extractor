# Builds the self-contained app and the Setup.exe installer.
#   powershell -ExecutionPolicy Bypass -File build.ps1
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$env:DOTNET_CLI_TELEMETRY_OPTOUT = 1
Push-Location $root

# No -o switch: the CLI turns it into an absolute path, and a comma in the folder name would be
# parsed by MSBuild as a property separator. The default publish folder is used, then copied.
dotnet publish src\MilesFontExtractor\MilesFontExtractor.csproj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=none -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

Remove-Item "$root\publish" -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory "$root\publish" | Out-Null
Copy-Item "$root\src\MilesFontExtractor\bin\Release\net8.0-windows\win-x64\publish\MilesFontExtractor.exe" "$root\publish\"

$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 not found" }
& $iscc "$root\installer\setup.iss"
if ($LASTEXITCODE -ne 0) { throw "installer build failed" }
Pop-Location
Get-ChildItem "$root\dist" | Select-Object Name, @{n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } }
