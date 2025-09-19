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
Copy-Item -Path "$ProjectDir\bin\release\netstandard2.0\Tomlyn.dll" -Destination "$LandisExtensionsDir\" -Force

cd $LandisExecutionDir
$landisExitCode = $null
try {
    Write-Output "Executing LANDIS-II..."
    landis-ii-8.cmd .\scenario.txt 2>&1 | Tee-Object -FilePath console-output.txt
    $landisExitCode = $LASTEXITCODE
    Write-Output "LANDIS-II execution completed."
    cd $ProjectDir\src\
} finally {
    cd $ProjectDir\src\
}
if ($landisExitCode -eq 0) {
    $framerate = 10

$vfMaxH264 = 'scale=w=min(iw\,16*floor(sqrt(139264*iw/ih))):h=min(ih\,16*floor(sqrt(139264*ih/iw))):force_original_aspect_ratio=decrease,scale=trunc(iw/2)*2:trunc(ih/2)*2,setsar=1'

ffmpeg -hide_banner -loglevel error -stats -y -framerate $framerate -i "$LandisExecutionDir/infection_timeline/infection_state_%d.png" -vf $vfMaxH264 -c:v libx264 -x264-params "level=6.2" -pix_fmt yuv420p "$LandisExecutionDir/infection_timeline.mp4"
Write-Output "Video saved to: $LandisExecutionDir/infection_timeline.mp4"

ffmpeg -hide_banner -loglevel error -stats -y -framerate $framerate -i "$LandisExecutionDir/infection_timeline_multi/infection_multi_state_%d.png" -vf $vfMaxH264 -c:v libx264 -x264-params "level=6.2" -pix_fmt yuv420p "$LandisExecutionDir/infection_timeline_multi.mp4"
Write-Output "Video saved to: $LandisExecutionDir/infection_timeline_multi.mp4"

ffmpeg -hide_banner -loglevel error -stats -y -framerate $framerate -i "$LandisExecutionDir/overall_timeline/overall_state_%d.png" -vf $vfMaxH264 -c:v libx264 -x264-params "level=6.2" -pix_fmt yuv420p "$LandisExecutionDir/overall_timeline.mp4"
Write-Output "Video saved to: $LandisExecutionDir/overall_timeline.mp4"

ffmpeg -hide_banner -loglevel error -stats -y -framerate $framerate -i "$LandisExecutionDir/foi_colourised_timeline/foi_colourised_state_%d.png" -vf $vfMaxH264 -c:v libx264 -x264-params "level=6.2" -pix_fmt yuv420p "$LandisExecutionDir/foi_colourised_timeline.mp4"
Write-Output "Video saved to: $LandisExecutionDir/foi_colourised_timeline.mp4"

ffmpeg -hide_banner -loglevel error -stats -y -framerate $framerate -i "$LandisExecutionDir/shim_timeline/shim_state_%d.png" -vf $vfMaxH264 -c:v libx264 -x264-params "level=6.2" -pix_fmt yuv420p "$LandisExecutionDir/shim_timeline.mp4"
Write-Output "Video saved to: $LandisExecutionDir/shim_timeline.mp4"

ffmpeg -hide_banner -loglevel error -stats -y -framerate $framerate -i "$LandisExecutionDir/shim_normalized_timeline/shim_normalized_state_%d.png" -vf $vfMaxH264 -c:v libx264 -x264-params "level=6.2" -pix_fmt yuv420p "$LandisExecutionDir/shim_normalized_timeline.mp4"
Write-Output "Video saved to: $LandisExecutionDir/shim_normalized_timeline.mp4"

ffmpeg -hide_banner -loglevel error -stats -y -framerate $framerate -i "$LandisExecutionDir/foi_timeline/foi_state_%d.png" -vf $vfMaxH264 -c:v libx264 -x264-params "level=6.2" -pix_fmt yuv420p "$LandisExecutionDir/foi_timeline.mp4"
Write-Output "Video saved to: $LandisExecutionDir/foi_timeline.mp4"

$capSide = 16 * [math]::Floor([math]::Sqrt(139264))
$tile    = [math]::Floor($capSide / 2)
$tilePad = "scale=w=${tile}:h=${tile}:force_original_aspect_ratio=decrease,scale=trunc(iw/2)*2:trunc(ih/2)*2,pad=${tile}:${tile}:(ow-iw)/2:(oh-ih)/2"

$fc = @"
[0:v]$tilePad[v0];
[1:v]$tilePad[v1];
[2:v]$tilePad[v2];
[3:v]$tilePad[v3];
[v0][v1][v2][v3]xstack=inputs=4:layout=0_0|${tile}_0|0_${tile}|${tile}_${tile}[out]
"@

ffmpeg -hide_banner -loglevel error -stats -y -i "$LandisExecutionDir/infection_timeline.mp4" -i "$LandisExecutionDir/infection_timeline_multi.mp4" -i "$LandisExecutionDir/overall_timeline.mp4" -i "$LandisExecutionDir/foi_colourised_timeline.mp4" -filter_complex $fc -map "[out]" -c:v libx264 -x264-params "level=6.2" -pix_fmt yuv420p "$LandisExecutionDir/quad_view.mp4"
Write-Output "Video saved to: $LandisExecutionDir/quad_view.mp4"

} else {
    Write-Output "Skipping video renders due to LANDIS-II non-zero exit code: $landisExitCode"
}

<# $framerate = 10
$panelSide = 4096
$finalMax = 8192
$crf = 18
$preset = "veryfast"
$finalSide = [math]::Min($panelSide * 2, $finalMax)

ffmpeg -y -framerate $framerate -i "$LandisExecutionDir/infection_timeline/infection_state_%d.png" -vf "scale=${panelSide}:${panelSide}:force_original_aspect_ratio=decrease:flags=lanczos,pad=${panelSide}:${panelSide}:(ow-iw)/2:(oh-ih)/2,setsar=1" -c:v libx264 -pix_fmt yuv420p -crf $crf -preset $preset -x264-params level=6.2 -movflags +faststart "$LandisExecutionDir/infection_timeline.mp4"
Write-Output "Video saved to: $LandisExecutionDir/infection_timeline.mp4"

ffmpeg -y -framerate $framerate -i "$LandisExecutionDir/shim_timeline/shim_state_%d.png" -vf "scale=${panelSide}:${panelSide}:force_original_aspect_ratio=decrease:flags=lanczos,pad=${panelSide}:${panelSide}:(ow-iw)/2:(oh-ih)/2,setsar=1" -c:v libx264 -pix_fmt yuv420p -crf $crf -preset $preset -x264-params level=6.2 -movflags +faststart "$LandisExecutionDir/shim_timeline.mp4"
Write-Output "Video saved to: $LandisExecutionDir/shim_timeline.mp4"

ffmpeg -y -framerate $framerate -i "$LandisExecutionDir/shim_normalized_timeline/shim_normalized_state_%d.png" -vf "scale=${panelSide}:${panelSide}:force_original_aspect_ratio=decrease:flags=lanczos,pad=${panelSide}:${panelSide}:(ow-iw)/2:(oh-ih)/2,setsar=1" -c:v libx264 -pix_fmt yuv420p -crf $crf -preset $preset -x264-params level=6.2 -movflags +faststart "$LandisExecutionDir/shim_normalized_timeline.mp4"
Write-Output "Video saved to: $LandisExecutionDir/shim_normalized_timeline.mp4"

ffmpeg -y -framerate $framerate -i "$LandisExecutionDir/foi_timeline/foi_state_%d.png" -vf "scale=${panelSide}:${panelSide}:force_original_aspect_ratio=decrease:flags=lanczos,pad=${panelSide}:${panelSide}:(ow-iw)/2:(oh-ih)/2,setsar=1" -c:v libx264 -pix_fmt yuv420p -crf $crf -preset $preset -x264-params level=6.2 -movflags +faststart "$LandisExecutionDir/foi_timeline.mp4"
Write-Output "Video saved to: $LandisExecutionDir/foi_timeline.mp4"

ffmpeg -y -i "$LandisExecutionDir/infection_timeline.mp4" -i "$LandisExecutionDir/shim_timeline.mp4" -i "$LandisExecutionDir/shim_normalized_timeline.mp4" -i "$LandisExecutionDir/foi_timeline.mp4" -filter_complex "[0:v][1:v][2:v][3:v]xstack=inputs=4:layout=0_0|${panelSide}_0|0_${panelSide}|${panelSide}_${panelSide}[g];[g]scale=${finalSide}:${finalSide}:force_original_aspect_ratio=decrease,setsar=1[final]" -map "[final]" -c:v libx264 -pix_fmt yuv420p -crf $crf -preset $preset -x264-params level=6.2 -movflags +faststart "$LandisExecutionDir/quad_view.mp4"
Write-Output "Video saved to: $LandisExecutionDir/quad_view.mp4" #>



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