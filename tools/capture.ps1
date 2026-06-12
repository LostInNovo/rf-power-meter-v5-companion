param(
    [int]$Baud = 460800,
    [int]$Seconds = 5,
    [switch]$Dtr,
    [string]$Send = "",
    [string]$OutFile = ""
)
$ErrorActionPreference = 'Stop'
$port = New-Object System.IO.Ports.SerialPort 'COM10', $Baud, 'None', 8, 'One'
$port.Handshake = 'None'
$port.DtrEnable = [bool]$Dtr
$port.RtsEnable = $false
$port.ReadTimeout = 200
$port.Open()
Start-Sleep -Milliseconds 200
$port.DiscardInBuffer()

if ($Send -ne "") {
    $sendBytes = [System.Text.Encoding]::ASCII.GetBytes($Send)
    $port.Write($sendBytes, 0, $sendBytes.Length)
    Write-Output ("SENT: " + (($sendBytes | ForEach-Object { $_.ToString('X2') }) -join ' '))
}

$buf = New-Object System.IO.MemoryStream
$deadline = (Get-Date).AddSeconds($Seconds)
while ((Get-Date) -lt $deadline) {
    $n = $port.BytesToRead
    if ($n -gt 0) {
        $tmp = New-Object byte[] $n
        $read = $port.Read($tmp, 0, $n)
        if ($read -gt 0) { $buf.Write($tmp, 0, $read) }
    } else {
        Start-Sleep -Milliseconds 20
    }
}
$port.Close()
$bytes = $buf.ToArray()
Write-Output ("BYTES RECEIVED: " + $bytes.Length + " in " + $Seconds + "s (" + [math]::Round($bytes.Length / $Seconds) + " B/s)")

if ($OutFile -ne "" -and $bytes.Length -gt 0) {
    [System.IO.File]::WriteAllBytes($OutFile, $bytes)
    Write-Output ("SAVED: " + $OutFile)
}

# Hex + ASCII dump of first 512 bytes
$limit = [math]::Min($bytes.Length, 512)
for ($i = 0; $i -lt $limit; $i += 16) {
    $chunk = $bytes[$i..([math]::Min($i + 15, $limit - 1))]
    $hex = ($chunk | ForEach-Object { $_.ToString('X2') }) -join ' '
    $ascii = ($chunk | ForEach-Object { if ($_ -ge 32 -and $_ -le 126) { [char]$_ } else { '.' } }) -join ''
    Write-Output ("{0:X4}  {1,-47}  {2}" -f $i, $hex, $ascii)
}
