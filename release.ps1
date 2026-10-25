# Usage: .\release.ps1 1.0.1   (builds, packs with delta vs previous release, uploads to GitHub Releases)
param([Parameter(Mandatory)][string]$Version)
$repo = "https://github.com/RDK456/Galary"
$ErrorActionPreference = "Stop"
dotnet publish Galary -c Release -r win-x64 --self-contained false -p:Version=$Version -o publish
vpk download github --repoUrl $repo -o releases   # previous full package -> enables delta
vpk pack --packId Galary --packVersion $Version --packDir publish --mainExe Galary.exe --runtime win-x64 --framework net8-x64-desktop -o releases
vpk upload github --repoUrl $repo -o releases --publish --releaseName "Galary $Version" --tag "v$Version" --token (gh auth token)
