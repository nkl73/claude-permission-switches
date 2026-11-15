[CmdletBinding()]
param([switch]$Test)

$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (!(Test-Path -LiteralPath $compiler)) {
    throw 'This build requires the 64-bit .NET Framework C# compiler.'
}

$dist = Join-Path $PSScriptRoot 'dist'
$null = New-Item -Path $dist -ItemType Directory -Force
$app = Join-Path $dist 'ClaudePermissions.exe'
$source = Join-Path $PSScriptRoot 'ClaudePermissions.cs'

$options = @(
    '/nologo', '/target:winexe', '/platform:anycpu', '/optimize+', '/codepage:65001'
    '/reference:System.Drawing.dll', '/reference:System.Windows.Forms.dll'
    '/reference:System.Web.Extensions.dll', '/reference:Accessibility.dll'
    "/out:$app", $source
)
& $compiler @options
if ($LASTEXITCODE) { throw "Build failed with exit code $LASTEXITCODE." }

if ($Test) {
    $testName = 'settings-test-' + [Guid]::NewGuid().ToString('N') + '.json'
    $testFile = Join-Path $dist $testName
    try {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'settings.example.json') -Destination $testFile
        $arguments = '--test-settings "' + $testFile + '"'
        $process = Start-Process -FilePath $app -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru
        if ($process.ExitCode) { throw "Settings test failed with exit code $($process.ExitCode)." }
        Write-Output 'All seven settings switches passed their round-trip test.'
    } finally {
        foreach ($file in @($testFile, "$testFile.switchboard-backup")) {
            if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file }
        }
    }
}
Write-Output $app
