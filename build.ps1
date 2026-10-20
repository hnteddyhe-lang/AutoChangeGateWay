param(
    [switch]$SelfContained,
    [ValidateSet('win-x64', 'win-arm64')][string]$Runtime = 'win-x64'
)
$ErrorActionPreference = 'Stop'
$env:MSBuildEnableWorkloadResolver = 'false'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.dotnet'
$env:NUGET_PACKAGES = Join-Path $PSScriptRoot '.packages'
Push-Location $PSScriptRoot
try {
    dotnet build AutoChangeGateWay.sln -c Release --ignore-failed-sources -p:NuGetAudit=false -m:1 -nr:false
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    dotnet tests/AutoChangeGateWay.Tests/bin/Release/net8.0/AutoChangeGateWay.Tests.dll
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    dotnet tests/AutoChangeGateWay.WindowsTests/bin/Release/net8.0-windows/AutoChangeGateWay.WindowsTests.dll (Join-Path $PSScriptRoot 'artifacts/validation/runtime-tests')
    if ($LASTEXITCODE -ne 0) { throw 'Windows runtime tests failed.' }
    $version = ([xml](Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
    $packageName = if ($SelfContained) { "AutoChangeGateWay-$version-$Runtime-standalone" } else { "AutoChangeGateWay-$version-$Runtime" }
    $output = Join-Path $PSScriptRoot "artifacts/$packageName"
    dotnet publish src/AutoChangeGateWay.App/AutoChangeGateWay.App.csproj -c Release -r $Runtime --self-contained $SelfContained.IsPresent.ToString().ToLowerInvariant() -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:NuGetAudit=false --ignore-failed-sources -m:1 -nr:false -o $output
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination (Join-Path $output 'README.md') -Force
    $documents = Join-Path $output 'docs'
    New-Item -ItemType Directory -Path $documents -Force | Out-Null
    Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'docs') | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $documents -Recurse -Force
    }
    if ($SelfContained) {
        $configuration = Join-Path $PSScriptRoot "src/AutoChangeGateWay.App/bin/Release/net8.0-windows/$Runtime/AutoChangeGateWay.runtimeconfig.json"
        $runtimeVersion = (Get-Content -LiteralPath $configuration -Raw | ConvertFrom-Json).runtimeOptions.includedFrameworks[0].version
        $notices = Join-Path $output 'notices'
        New-Item -ItemType Directory -Path $notices -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $env:NUGET_PACKAGES "microsoft.netcore.app.runtime.$Runtime/$runtimeVersion/LICENSE.TXT") -Destination (Join-Path $notices 'DOTNET-LICENSE.txt') -Force
        Copy-Item -LiteralPath (Join-Path $env:NUGET_PACKAGES "microsoft.netcore.app.runtime.$Runtime/$runtimeVersion/THIRD-PARTY-NOTICES.TXT") -Destination (Join-Path $notices 'DOTNET-THIRD-PARTY-NOTICES.txt') -Force
        Copy-Item -LiteralPath (Join-Path $env:NUGET_PACKAGES "microsoft.windowsdesktop.app.runtime.$Runtime/$runtimeVersion/LICENSE") -Destination (Join-Path $notices 'WINDOWS-DESKTOP-LICENSE.txt') -Force
        Get-ChildItem -LiteralPath (Join-Path $env:NUGET_PACKAGES 'system.serviceprocess.servicecontroller/8.0.1') -File |
            Where-Object { $_.Name -match 'LICENSE|NOTICE' } | ForEach-Object {
                Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $notices ('SERVICE-CONTROLLER-' + $_.Name)) -Force
            }
    }
    Compress-Archive -LiteralPath $output -DestinationPath ($output + '.zip') -Force
    (Get-FileHash -LiteralPath ($output + '.zip') -Algorithm SHA256).Hash | Set-Content -LiteralPath ($output + '.zip.sha256') -Encoding ascii
    Write-Host "Package: $output.zip"
}
finally { Pop-Location }
