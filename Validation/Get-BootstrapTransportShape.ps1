[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackageDirectory,
    [ValidateSet('Bootstrap','Empty','Malformed','Null','Unknown')][string]$RequestKind='Bootstrap'
)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath($PackageDirectory)
$script=Join-Path $root 'setup\Invoke-ScomSetupGui.ps1'
if (-not (Test-Path -LiteralPath $script -PathType Leaf)) { throw 'Bridge entrypoint missing.' }
$start=[Diagnostics.ProcessStartInfo]::new()
$start.FileName=Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$start.Arguments='-NoProfile -File "'+$script+'"'
$start.WorkingDirectory=$root
$start.UseShellExecute=$false
$start.CreateNoWindow=$true
$start.RedirectStandardInput=$true
$start.RedirectStandardOutput=$true
$start.RedirectStandardError=$true
$utf8=[Text.UTF8Encoding]::new($false)
$start.StandardOutputEncoding=$utf8
$start.StandardErrorEncoding=$utf8
$retained=@('SystemRoot','WINDIR','SystemDrive','COMSPEC','TEMP','TMP','USERPROFILE','APPDATA','LOCALAPPDATA',
    'ProgramData','ProgramFiles','ProgramFiles(x86)','ProgramW6432','USERNAME','USERDOMAIN','HOMEDRIVE','HOMEPATH')
$start.EnvironmentVariables.Clear()
foreach ($name in $retained) {
    $value=[Environment]::GetEnvironmentVariable($name)
    if ($null -ne $value) { $start.EnvironmentVariables[$name]=$value }
}
$start.EnvironmentVariables['PATH']=(Join-Path $env:SystemRoot 'System32')+';'+$env:SystemRoot+';'+
    (Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'dotnet')
$requests=@{Bootstrap='{"action":"Bootstrap"}';Empty='';Malformed='{';Null='null';Unknown='{"action":"NotAnAction"}'}
$process=[Diagnostics.Process]::new()
$process.StartInfo=$start
$previous=[Console]::InputEncoding
try {
    # Desktop Process uses Console.InputEncoding for redirected stdin. Never emit its UTF-8 BOM.
    [Console]::InputEncoding=$utf8
    $null=$process.Start()
} finally { [Console]::InputEncoding=$previous }
try {
    $stdout=$process.StandardOutput.ReadToEndAsync()
    $stderr=$process.StandardError.ReadToEndAsync()
    try { $process.StandardInput.WriteLine($requests[$RequestKind]) } finally { $process.StandardInput.Close() }
    if (-not $process.WaitForExit(120000)) {
        Stop-Process -Id $process.Id
        throw 'Read-only Bootstrap timed out; no raw output displayed.'
    }
    $text=$stdout.GetAwaiter().GetResult()
    $null=$stderr.GetAwaiter().GetResult()
    $known=@('ok','code','message','checks','groups','stage','pendingAction','approvalRequired','summary','plan',
        'defaults','stateDirectory','sites','pools','certificates','currentAccount','managementGroupId')
    $report=[ordered]@{exitCode=$process.ExitCode;rootKind='invalid-json';propertyCount=0;unknownPropertyCount=0;properties=@()}
    if ($text.Length -le 2MB) {
        try {
            # JavaScriptSerializer preserves object versus array and exact key casing on Windows PowerShell 5.1.
            Add-Type -AssemblyName System.Web.Extensions
            $parser=[Web.Script.Serialization.JavaScriptSerializer]::new()
            $parser.MaxJsonLength=2MB
            $value=$parser.DeserializeObject($text)
            $report.rootKind=if ($null -eq $value) {'null'} elseif ($value -is [Collections.IDictionary]) {'object'} elseif ($value -is [array]) {'array'} else {'scalar'}
            if ($value -is [Collections.IDictionary]) {
                $report.propertyCount=$value.Count
                $report.properties=@(foreach ($key in $value.Keys) {
                    if ($key -cnotin $known) { $report.unknownPropertyCount++;continue }
                    $v=$value[$key]
                    $kind=if ($null -eq $v) {'null'} elseif ($v -is [bool]) {'boolean'} elseif ($v -is [string]) {'string'} elseif ($v -is [Collections.IDictionary]) {'object'} elseif ($v -is [array]) {'array'} else {'number'}
                    [pscustomobject]@{name=$key;type=$kind}
                })
            }
        } catch { $report.rootKind='invalid-json' }
    } else { $report.rootKind='oversized' }
    [pscustomobject]$report | ConvertTo-Json -Depth 4 -Compress
} finally { $process.Dispose() }
