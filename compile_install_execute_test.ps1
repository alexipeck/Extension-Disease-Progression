param(
    [Parameter(Mandatory=$false)]
    [string]$ProjectDir = $PSScriptRoot,
    
    [Parameter(Mandatory=$true)]
    [string]$ScenarioDirectory
)

$OriginalDir = "$PSScriptRoot"

if (-not (Test-Path $ScenarioDirectory)) {
    Write-Host "Error: Scenario directory '$ScenarioDirectory' does not exist."
    exit 1
}

if (-NOT ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole] "Administrator")) {
    Write-Host "This script requires administrative privileges. Please run PowerShell as Administrator."
    exit
}

$LandisExtensionsDir = "C:\Program Files\LANDIS-II-v8\extensions"
$LandisExecutionDir = $ScenarioDirectory

Write-Host "Configuration:"
Write-Host "  Project Directory: $ProjectDir"
Write-Host "  LANDIS-II Execution Directory: $LandisExecutionDir"
Write-Host ""

cd $ProjectDir\src\
dotnet build -c release
if ($LASTEXITCODE -ne 0) {
    Write-Host "dotnet build failed. Exiting script."
    exit $LASTEXITCODE
}

Copy-Item -Path "$ProjectDir\bin\release\netstandard2.0\Landis.Extension.Disturbance.DiseaseProgression.dll" -Destination "$LandisExtensionsDir\" -Force

cd $LandisExecutionDir
try {
    Write-Output "Executing LANDIS-II..."
    landis-ii-8.cmd .\scenario.txt 2>&1 | Tee-Object -FilePath console-output.txt
    Write-Output "LANDIS-II execution completed."
    cd $ProjectDir\src\
} finally {
    cd $ProjectDir\src\
}

Write-Output "Turning image sequences into videos..."
ffmpeg -y -framerate 10 -i "$LandisExecutionDir/infection_timeline/infection_state_%d.png" -c:v libx264 -pix_fmt yuv420p "$LandisExecutionDir/infection_timeline.mp4"
Write-Output "Video saved to: $LandisExecutionDir/infection_timeline.mp4"
ffmpeg -y -framerate 10 -i "$LandisExecutionDir/shi_timeline/shi_state_%d.png" -c:v libx264 -pix_fmt yuv420p "$LandisExecutionDir/shi_timeline.mp4"
Write-Output "Video saved to: $LandisExecutionDir/shi_timeline.mp4"
ffmpeg -y -framerate 10 -i "$LandisExecutionDir/shim_timeline/shim_state_%d.png" -c:v libx264 -pix_fmt yuv420p "$LandisExecutionDir/shim_timeline.mp4"
Write-Output "Video saved to: $LandisExecutionDir/shim_timeline.mp4"
ffmpeg -y -framerate 10 -i "$LandisExecutionDir/shim_normalized_timeline/shim_normalized_state_%d.png" -c:v libx264 -pix_fmt yuv420p "$LandisExecutionDir/shim_normalized_timeline.mp4"
Write-Output "Video saved to: $LandisExecutionDir/shim_normalized_timeline.mp4"
ffmpeg -y -framerate 10 -i "$LandisExecutionDir/foi_timeline/foi_state_%d.png" -c:v libx264 -pix_fmt yuv420p "$LandisExecutionDir/foi_timeline.mp4"
Write-Output "Video saved to: $LandisExecutionDir/foi_timeline.mp4"

Write-Output "Creating filtered console output..."
$consoleOutputPath = "$LandisExecutionDir\console-output.txt"
$filteredOutputPath = "$LandisExecutionDir\console-output-filtered.txt"

if (Test-Path $consoleOutputPath) {
    Get-Content $consoleOutputPath | Where-Object { 
        $_ -match "^Before disease progression" -or $_ -match "^\(inert\) Transitioned to dead" -or $_ -match "^Transitioned to dead" -or $_ -match "^Transferred" -or $_ -match "^After disease progression" -or $_ -match "^Current time" -or $_ -match "^Transitioning"
    } | ForEach-Object {
        if ($_ -match "^Adding new cohort:" -or $_ -match "^Transferring from") {
            "    $_"
        } else {
            $_
        }
    } | Out-File -FilePath $filteredOutputPath -Encoding UTF8
    Write-Output "Filtered console output saved to: $filteredOutputPath"
} else {
    Write-Output "console-output.txt not found. Skipping filtering."
}

cd $OriginalDir