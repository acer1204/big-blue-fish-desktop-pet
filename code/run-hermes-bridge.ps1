# 啟動 Hermes 對話橋接：雙擊魚魚輸入的訊息會交給 Hermes，回答顯示在魚魚的氣泡。
# 需要：桌寵已開啟「MCP 介面」與「雙擊與魚魚對話」，Hermes 的 .env 已設定 API_SERVER_KEY 並啟動 gateway。
param([string]$SdkPath,[string]$McpCommand,[string]$HermesUrl)
$ErrorActionPreference='Stop'
$sdk=& "$PSScriptRoot/tools/Resolve-Sdk.ps1" -SdkPath $SdkPath
$project=Join-Path $PSScriptRoot 'BigBlueFish.HermesBridge/BigBlueFish.HermesBridge.csproj'
& $sdk build $project -c Release -p:RestoreLockedMode=true --nologo -v q
if($LASTEXITCODE -ne 0){throw '橋接程式編譯失敗'}
$bridgeArgs=@()
if($McpCommand){$bridgeArgs+=@('--mcp',$McpCommand)}
if($HermesUrl){$bridgeArgs+=@('--hermes-url',$HermesUrl)}
# Without an .exe of its own (UseAppHost=false, see the project): the process shows up as dotnet running this dll.
& $sdk (Join-Path $PSScriptRoot 'BigBlueFish.HermesBridge/bin/Release/net10.0-windows/BigBlueFish.HermesBridge.dll') @bridgeArgs
