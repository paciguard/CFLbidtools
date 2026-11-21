param(
    [ValidateSet('Windows','Android','iOS')][string[]]$Platform = @('Windows'),
    [string]$InnoCompiler = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    [string]$AndroidSdk = "${env:ProgramFiles(x86)}\Android\android-sdk",
    [string]$JavaSdk = "${env:ProgramFiles(x86)}\Android\openjdk\jdk-17.0.14",
    [string]$AndroidBuildRoot = $env:CFL_ANDROID_BUILD_ROOT,
    [string]$AppleSigningKey = $env:CFL_APPLE_SIGNING_KEY,
    [string]$AppleProvision = $env:CFL_APPLE_PROVISION,
    [switch]$FromRebuild
)
$ErrorActionPreference = 'Stop'
if ($FromRebuild) {
    if ($IsMacOS) {
        if (!$AppleSigningKey -or !$AppleProvision) {
            Write-Host 'iPhone package skipped: configure CFL_APPLE_SIGNING_KEY and CFL_APPLE_PROVISION on this Mac.'
            return
        }
        $Platform = @('iOS')
    }
    else {
        $Platform = @('Windows', 'Android')
        Write-Host 'Rebuild packages Windows and Android. A signed iPhone package requires macOS and Apple signing.'
    }
}
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$packages = Join-Path $root 'artifacts\packages'
$build = Join-Path $root 'artifacts\package-build'
$mobile = Join-Path $PSScriptRoot 'Mobile\CflWorkbench.Mobile.csproj'
New-Item -ItemType Directory -Force -Path $packages | Out-Null

function Run-Dotnet {
    & dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "Package build failed (exit $LASTEXITCODE)." }
}

if ($Platform -contains 'Windows') {
    if (!(Test-Path -LiteralPath $InnoCompiler)) { throw 'Set -InnoCompiler to the Inno Setup compiler.' }
    $publish = Join-Path $build 'windows-publish'
    Run-Dotnet publish (Join-Path $root 'CflWorkbench.csproj') -c Release -r win-x64 --self-contained true `
        '-p:CflPackageOnRebuild=false' --artifacts-path $build -o $publish --ignore-failed-sources
    & $InnoCompiler "/DPublishDir=$publish" "/DPackageDir=$packages" (Join-Path $PSScriptRoot 'CFLbidtools.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Windows setup compilation failed.' }
}
if ($Platform -contains 'Android') {
    if (!(Test-Path -LiteralPath $AndroidSdk) -or !(Test-Path -LiteralPath $JavaSdk)) {
        throw 'Set -AndroidSdk and -JavaSdk to your installed Android SDK and JDK.'
    }
    # AAPT2 cannot read asset directories under some non-ASCII Windows paths.
    if (!$AndroidBuildRoot) { $AndroidBuildRoot = Join-Path ([IO.Path]::GetTempPath()) 'CFLAndroid' }
    $AndroidBuildRoot = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($AndroidBuildRoot)
    if ($AndroidBuildRoot -match '[^\x20-\x7E]') { throw 'Set CFL_ANDROID_BUILD_ROOT to a short build folder without non-ASCII characters.' }
    $hash = [Security.Cryptography.SHA256]::Create()
    try { $slug = [BitConverter]::ToString($hash.ComputeHash([Text.Encoding]::UTF8.GetBytes($root))).Replace('-', '').Substring(0, 16) }
    finally { $hash.Dispose() }
    $androidBuild = Join-Path $AndroidBuildRoot $slug
    $publish = Join-Path $androidBuild 'publish'
    # The Android manifest merger reads UTF-8 response files containing project paths.
    Run-Dotnet publish $mobile -c Release "-p:AndroidSdkDirectory=$AndroidSdk" "-p:JavaSdkDirectory=$JavaSdk" `
        '-p:JavaOptions=-Dfile.encoding=UTF-8' '-p:AndroidPackageFormats=apk' '-p:RunAOTCompilation=false' `
        --artifacts-path $androidBuild -o $publish --ignore-failed-sources
    $apk = Get-ChildItem -LiteralPath $publish -Filter '*-Signed.apk' | Select-Object -First 1
    if (!$apk) { throw 'No signed Android test APK was produced.' }
    Copy-Item -LiteralPath $apk.FullName -Destination (Join-Path $packages 'CFLbidtools-Android-Test.apk') -Force
}
if ($Platform -contains 'iOS') {
    if (!$IsMacOS -or !$AppleSigningKey -or !$AppleProvision) {
        throw 'iOS packaging requires macOS with Xcode, an Apple signing identity and a provisioning profile.'
    }
    Run-Dotnet publish $mobile -c Release '-p:TargetFramework=net8.0-ios' -r ios-arm64 '-p:BuildIpa=true' --artifacts-path $build `
        "-p:CodesignKey=$AppleSigningKey" "-p:CodesignProvision=$AppleProvision" -o (Join-Path $packages 'ios')
}
