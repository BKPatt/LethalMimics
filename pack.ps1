# Builds both plugins and produces release zips in .\dist
#   dist\LethalMimics-<ver>.zip    Thunderstore package (recorded-clip mimics; no AI)
#   dist\LethalMimicsAI-<ver>.zip  AI add-on for GitHub releases (Thunderstore layout, so mod managers can "Import local mod")
#
#   .\pack.ps1            build + zip
#   .\pack.ps1 -Deploy    also install both into the mod-manager profile set in LethalMimics.local.props
param([switch]$Deploy)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

function Build($project) {
    $buildArgs = @($project, '-c', 'Release')
    if ($Deploy) { $buildArgs += '-p:Deploy=true' }
    dotnet build @buildArgs
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $project" }
}

function Read-Manifest($dir) {
    $m = Get-Content "$dir\manifest.json" -Raw | ConvertFrom-Json
    if ($m.name -notmatch '^[A-Za-z0-9_]+$') { throw "$dir manifest name must be letters, digits, underscore" }
    if ($m.description.Length -gt 250) { throw "$dir manifest description is $($m.description.Length) chars (max 250)" }
    if ($m.version_number -notmatch '^\d+\.\d+\.\d+$') { throw "$dir version_number must be Major.Minor.Patch" }
    foreach ($f in 'README.md', 'CHANGELOG.md', 'icon.png') { if (-not (Test-Path "$dir\$f")) { throw "$dir\$f is missing" } }
    return $m
}

# Zip entries must use forward slashes (Compress-Archive in Windows PowerShell writes backslashes, which breaks the zip spec).
function Write-Zip($stage, $zip) {
    if (Test-Path $zip) { Remove-Item $zip -Force }
    $archive = [System.IO.Compression.ZipFile]::Open($zip, 'Create')
    try {
        foreach ($file in Get-ChildItem $stage -Recurse -File) {
            $entry = $file.FullName.Substring($stage.Length + 1).Replace('\', '/')
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $entry, 'Optimal') | Out-Null
        }
    } finally { $archive.Dispose() }
    Write-Host "Package: $zip"
}

function Pack($packageDir, $pluginFolder, [string[]]$pluginFiles, [hashtable]$extraFiles) {
    $m = Read-Manifest $packageDir
    $stage = "$root\dist\stage-$($m.name)"
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    $plugin = "$stage\BepInEx\plugins\$pluginFolder"
    New-Item -ItemType Directory -Force $plugin | Out-Null
    Copy-Item "$packageDir\manifest.json", "$packageDir\README.md", "$packageDir\CHANGELOG.md", "$packageDir\icon.png" $stage
    Copy-Item $pluginFiles $plugin
    foreach ($sub in $extraFiles.Keys) {
        New-Item -ItemType Directory -Force "$plugin\$sub" | Out-Null
        Copy-Item $extraFiles[$sub] "$plugin\$sub"
    }
    Write-Zip $stage "$root\dist\$($m.name)-$($m.version_number).zip"
    Remove-Item $stage -Recurse -Force
}

Build "$root\addon\LethalMimicsAI.csproj"   # builds LethalMimics.csproj first (project reference)
New-Item -ItemType Directory -Force "$root\dist" | Out-Null

Pack "$root\package" 'LethalMimics' @("$root\bin\Release\LethalMimics.dll") @{}
Pack "$root\addon\package" 'LethalMimicsAI' @("$root\addon\bin\Release\LethalMimicsAI.dll") @{
    'ai_server' = @("$root\addon\ai_server\server.py", "$root\addon\ai_server\requirements.txt", "$root\addon\ai_server\setup.ps1")
}
