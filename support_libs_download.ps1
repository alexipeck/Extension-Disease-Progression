# LANDIS-II support library GitHub URL
$master = "https://github.com/LANDIS-II-Foundation/Support-Library-Dlls-v8/raw/main/"


#************************************************
# LANDIS-II support library dependencies
# Modify here when any dependencies changed 

$dlls = "Landis.Library.UniversalCohorts-v1.dll"
#************************************************


# LANDIS-II support libraries download
$current = Get-Location
$outpath = $current.toString() + "/lib/"
if (-Not (Test-Path -Path $outpath)) {
    New-Item -ItemType Directory -Path $outpath | Out-Null
}

try {
	ForEach ($item in $dlls) {
		$dll = $outpath + $item
		$url = $master + $item
		[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
		Invoke-WebRequest -uri $url -Outfile $dll
		($dll).split('/')[-1].toString() + "------------- downloaded"
	}
	"`n***** Download complete *****`n"
}
catch [System.Net.WebException],[System.IO.IOException]{
	"Unable to download file from " + $item.toString()
}
catch {
	"An error occurred."
}

