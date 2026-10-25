# Builds a ready-to-copy Galary folder in .\dist (needs the .NET 8 Desktop Runtime on the target PC)
dotnet publish Galary -c Release -r win-x64 --self-contained false -o dist
Write-Host "Done: run dist\Galary.exe"
