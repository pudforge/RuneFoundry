# Builds the release zip: both programs, one folder, one download.
#
# Publishing by hand is how the wrong thing ships. This starts from an empty folder every
# time, so output left over from an earlier build cannot ride along, and it names the zip
# after the version in Directory.Build.props so two downloads are never confused.
#
#     powershell -File tools\package.ps1
#
# The two programs share one copy of .NET and WPF because they publish into the same
# folder. Two single-file executables would each carry their own: 126 MB zipped against 67.

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
Push-Location $root

try {
    $props = [xml](Get-Content "Directory.Build.props")
    $version = $props.Project.PropertyGroup.Version
    if (-not $version) { throw "No <Version> in Directory.Build.props." }

    $stage = "dist\RuneFoundry"
    $zip = "dist\RuneFoundry-$version.zip"

    Write-Host "RuneFoundry $version"

    # The tests decide whether this is fit to ship, so they run before anything is built
    # for release rather than after.
    Write-Host "`nrunning the tests..."
    $tests = dotnet run --project tools\RuneFoundry.Tests\RuneFoundry.Tests.csproj -c Release 2>&1 | Out-String
    $summary = ($tests -split "`n" | Select-String -Pattern "passed|failed" | Select-Object -Last 1).ToString().Trim()

    # The runner's exit code, not its output: several test names contain the word "fails",
    # and matching on that turned a clean run into a failed release.
    if ($LASTEXITCODE -ne 0) {
        Write-Host $tests
        throw "Tests failed. Nothing was packaged."
    }
    Write-Host "  $summary"

    # The views as well as the formats. These build windows off screen, drive the real
    # controls and read them back, which catches what the unit tests cannot see: a list
    # that never fills, a dropdown with nothing in it, a note that lost half its meaning.
    Write-Host "`nrunning the UI checks..."
    $ui = dotnet run --project tools\RuneFoundry.UiCheck\RuneFoundry.UiCheck.csproj -c Release 2>&1 | Out-String

    if ($LASTEXITCODE -ne 0) {
        Write-Host $ui
        throw "UI checks failed. Nothing was packaged."
    }

    Write-Host "  $((($ui -split "`n" | Select-String -Pattern "UI checks") | Select-Object -Last 1).ToString().Trim())"

    # A copy of what we are about to overwrite, still running, holds its own DLLs open and
    # the delete below fails with a permission error that names clrjit.dll and explains
    # nothing. Say what is actually happening.
    $running = Get-Process | Where-Object { $_.Path -and $_.Path.StartsWith((Resolve-Path $stage -ErrorAction SilentlyContinue)) }
    if ($running) {
        $names = ($running | ForEach-Object { "$($_.ProcessName) (pid $($_.Id))" }) -join ", "
        throw "Close the running programs first: $names. They hold files this build replaces."
    }

    # From empty: a stale file in the output folder is a file in the download.
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    if (Test-Path $zip) { Remove-Item $zip -Force }

    foreach ($project in "RuneFoundry.Editor", "RuneFoundry.Launcher") {
        Write-Host "publishing $project..."
        dotnet publish "src\$project\$project.csproj" -c Release -o $stage --nologo | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "$project failed to publish." }
    }

    # Debug symbols are for the machine that built it, not for the download.
    Get-ChildItem $stage -Filter *.pdb -Recurse | Remove-Item -Force

    # A download of 469 files with nothing to read is a download nobody trusts.
    Copy-Item "packaging\README.txt" $stage

    foreach ($exe in "RuneFoundry Editor.exe", "RuneFoundry Launcher.exe", "README.txt") {
        if (-not (Test-Path (Join-Path $stage $exe))) { throw "$exe is missing from the build." }
    }

    Compress-Archive -Path "$stage\*" -DestinationPath $zip -CompressionLevel Optimal

    $size = (Get-Item $zip).Length / 1MB
    $files = (Get-ChildItem $stage -Recurse -File).Count

    Write-Host ("`n{0}" -f $zip)
    Write-Host ("  {0:N0} files, {1:N1} MB zipped" -f $files, $size)

    # Standalone executables as well. Each carries its own copy of .NET and WPF, so the two
    # together are far larger than the zip, and each unpacks itself on first run. They are
    # for people who want one file rather than a folder, not the recommended download.
    Write-Host "`nbuilding standalone executables..."
    $single = "dist\standalone"
    if (Test-Path $single) { Remove-Item $single -Recurse -Force }

    foreach ($project in "RuneFoundry.Editor", "RuneFoundry.Launcher") {
        dotnet publish "src\$project\$project.csproj" -c Release -o $single --nologo `
            -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
            -p:EnableCompressionInSingleFile=true -p:DebugType=None | Out-Null

        if ($LASTEXITCODE -ne 0) { throw "$project failed to publish standalone." }
    }

    foreach ($exe in Get-ChildItem $single -Filter *.exe | Where-Object { $_.Name -ne "createdump.exe" }) {
        $named = "dist\{0} {1}.exe" -f $exe.BaseName, $version
        Copy-Item $exe.FullName $named -Force
        Write-Host ("  {0}  {1:N1} MB" -f $named, ($exe.Length / 1MB))
    }

    Remove-Item $single -Recurse -Force
}
finally {
    Pop-Location
}
