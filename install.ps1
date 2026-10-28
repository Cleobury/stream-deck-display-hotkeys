# Builds the plugin and installs it into Stream Deck (restarts the Stream Deck app).
$ErrorActionPreference = 'Stop'
$plugin = "$PSScriptRoot\com.lee-cleobury.displayhotkeys.sdPlugin"
$dest = "$env:APPDATA\Elgato\StreamDeck\Plugins\com.lee-cleobury.displayhotkeys.sdPlugin"

& "$PSScriptRoot\make-icons.ps1"
dotnet publish "$PSScriptRoot\DisplayHotkeys" -c Release -r win-x64 --self-contained false `
    -p:PublishSingleFile=true -p:DebugType=none -o "$plugin\bin"
if ($LASTEXITCODE -ne 0) { throw "Build failed" }

Stop-Process -Name StreamDeck -Force -ErrorAction SilentlyContinue
Stop-Process -Name DisplayHotkeys -Force -ErrorAction SilentlyContinue
Wait-Process -Name StreamDeck, DisplayHotkeys -Timeout 15 -ErrorAction SilentlyContinue
Start-Sleep 1
New-Item -ItemType Directory -Force $dest | Out-Null
Copy-Item "$plugin\*" $dest -Recurse -Force
Start-Process "${env:ProgramFiles}\Elgato\StreamDeck\StreamDeck.exe"
Write-Host "Installed. Stream Deck is restarting."
