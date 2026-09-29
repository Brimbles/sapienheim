# Starts the local Valheim Dedicated Server (with BepInEx) on a private dev world.
# BepInEx log: <server>\BepInEx\LogOutput.log
param(
    [string]$ServerPath = "C:\Program Files (x86)\Steam\steamapps\common\Valheim dedicated server",
    [string]$World = "SapienDev",
    [string]$Password = $(if ($env:DEV_SERVER_PASSWORD) { $env:DEV_SERVER_PASSWORD } else { "sapiendev" }),
    [int]$Port = 2456
)

$env:SteamAppId = "892970"
Set-Location $ServerPath
& .\valheim_server.exe -nographics -batchmode -name "Sapienheim Dev" -port $Port -world $World -password $Password -public 0
