# Builds the Microsoft Store package: out/store/Loupix_<version>.msixbundle, ready to upload in Partner Center.
# It's left unsigned on purpose, because the Store signs it.
#
#   pwsh packaging/store/build.ps1                     every architecture, Release
#   pwsh packaging/store/build.ps1 -Arch x64 -Configuration Debug -LayoutOnly
#       just the unpacked folder, to try with Add-AppxPackage -Register (needs Developer Mode)

param(
    [string[]]$Arch = @('x64', 'arm64'),
    [string]$Configuration = 'Release',
    [string]$OutDir = 'out/store',
    [switch]$LayoutOnly
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path "$PSScriptRoot/../..").Path
Set-Location $repo
$OutDir = Join-Path $repo $OutDir

# The Store wants a four-part version whose last part is 0.
$version = ([xml](Get-Content Directory.Build.props)).Project.PropertyGroup.Version + '.0'

$kit = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\10.*\x64\makeappx.exe" | Sort-Object FullName | Select-Object -Last 1
if (-not $kit) { throw 'makeappx.exe not found. Install the Windows SDK.' }
$makeappx = $kit.FullName
$makepri = Join-Path $kit.DirectoryName 'makepri.exe'

function Run([string]$exe, [string[]]$arguments) {
    & $exe @arguments | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "$(Split-Path $exe -Leaf) failed with exit code $LASTEXITCODE" }
}

$packages = Join-Path $OutDir 'packages'
New-Item -ItemType Directory -Force $packages | Out-Null

foreach ($a in $Arch) {
    $layout = Join-Path $OutDir "layout-$a"
    if (Test-Path $layout) { Remove-Item $layout -Recurse -Force }

    # Not a single file: inside a package, there's no reason to unpack the app on every launch.
    dotnet publish src/Loupix/Loupix.csproj -c $Configuration -r "win-$a" --self-contained -o $layout -v quiet
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
    if ($Configuration -eq 'Release') { Get-ChildItem $layout -Filter *.pdb -Recurse | Remove-Item }
    Copy-Item LICENSE, THIRD-PARTY-NOTICES.md $layout

    Copy-Item packaging/store/Images (Join-Path $layout 'Images') -Recurse
    $manifest = (Get-Content packaging/store/AppxManifest.xml -Raw).Replace('$Version$', $version).Replace('$Arch$', $a)
    Set-Content (Join-Path $layout 'AppxManifest.xml') $manifest -Encoding utf8

    # The index that lets Windows pick the right icon size.
    $config = Join-Path $OutDir "priconfig-$a.xml"
    Run $makepri @('createconfig', '/cf', $config, '/dq', 'en-us', '/pv', '10.0.0', '/o')
    Run $makepri @('new', '/pr', $layout, '/cf', $config, '/of', (Join-Path $layout 'resources.pri'), '/mn', (Join-Path $layout 'AppxManifest.xml'), '/o')

    if (-not $LayoutOnly) { Run $makeappx @('pack', '/d', $layout, '/p', (Join-Path $packages "Loupix_${version}_$a.msix"), '/o') }
}

if ($LayoutOnly) { return }
$bundle = Join-Path $OutDir "Loupix_$version.msixbundle"
Run $makeappx @('bundle', '/d', $packages, '/p', $bundle, '/bv', $version, '/o')
Write-Output "Built $bundle ($([math]::Round((Get-Item $bundle).Length / 1MB)) MB)"
