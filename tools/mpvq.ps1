# Query the embedded mpv of a running FluxTube over its IPC pipe. Usage: mpvq.ps1 time-pos duration pause path
param([string]$Match = "*", [Parameter(ValueFromRemainingArguments)][string[]]$Props = @("time-pos", "duration", "pause", "media-title", "video-params/h", "estimated-vf-fps", "demuxer-cache-duration"))
$p = Get-Process FluxTube -ErrorAction Stop | Where-Object { $_.Path -like $Match } | Select-Object -First 1
$pipe = New-Object System.IO.Pipes.NamedPipeClientStream(".", "fluxtube-mpv-$($p.Id)", [System.IO.Pipes.PipeDirection]::InOut)
$pipe.Connect(2000)
$w = New-Object System.IO.StreamWriter($pipe); $w.AutoFlush = $true
$r = New-Object System.IO.StreamReader($pipe)
$i = 0
foreach ($prop in $Props) {
    $i++
    $w.WriteLine((@{ command = @("get_property", $prop); request_id = $i } | ConvertTo-Json -Compress))
    while ($true) {
        $line = $r.ReadLine()
        if ($line -match "`"request_id`":$i\b") { "{0,-24} {1}" -f $prop, (($line | ConvertFrom-Json).data); break }
    }
}
$pipe.Dispose()
