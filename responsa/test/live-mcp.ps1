# IT-780: live end-to-end test through the MCP protocol. Needs Responsa installed (and its disk connected).
# Starts dist\responsa-mcp.exe, sends JSON-RPC lines on stdin, prints the replies (shortened).
param([int]$Width = 700)
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe = Join-Path $here '..\dist\responsa-mcp.exe'
$calls = @(
  @{ jsonrpc = '2.0'; id = 1; method = 'initialize'; params = @{ protocolVersion = '2024-11-05'; capabilities = @{}; clientInfo = @{ name = 'live-test'; version = '1' } } },
  @{ jsonrpc = '2.0'; method = 'notifications/initialized' },
  @{ jsonrpc = '2.0'; id = 2; method = 'tools/list' },
  @{ jsonrpc = '2.0'; id = 3; method = 'tools/call'; params = @{ name = 'search'; arguments = @{ query = '#מקוה [1:3] #טהרה'; all_databases = $true; limit = 3 } } },
  @{ jsonrpc = '2.0'; id = 4; method = 'tools/call'; params = @{ name = 'get_results'; arguments = @{ search_id = 1; from = 101; count = 2 } } },
  @{ jsonrpc = '2.0'; id = 5; method = 'tools/call'; params = @{ name = 'search'; arguments = @{ query = 'נר [3:1] שבת' } } },
  @{ jsonrpc = '2.0'; id = 6; method = 'tools/call'; params = @{ name = 'search'; arguments = @{ query = 'שטר' ; all_databases = $true } } },
  @{ jsonrpc = '2.0'; id = 7; method = 'tools/call'; params = @{ name = 'search'; arguments = @{ query = 'קקקזזזפפפ'; all_databases = $true } } },
  @{ jsonrpc = '2.0'; id = 8; method = 'tools/call'; params = @{ name = 'open_text'; arguments = @{ reference = 'שוע אוח רסג'; max_chars = 800 } } },
  @{ jsonrpc = '2.0'; id = 9; method = 'tools/call'; params = @{ name = 'open_text'; arguments = @{ reference = 'שבת ד ב'; max_chars = 300 } } },
  @{ jsonrpc = '2.0'; id = 10; method = 'tools/call'; params = @{ name = 'open_text'; arguments = @{ reference = 'בבלי ברכות סה א' } } },
  @{ jsonrpc = '2.0'; id = 11; method = 'tools/call'; params = @{ name = 'search'; arguments = @{ query = 'x'; limit = 99 } } },
  @{ jsonrpc = '2.0'; id = 12; method = 'tools/call'; params = @{ name = 'status'; arguments = @{} } },
  @{ jsonrpc = '2.0'; id = 13; method = 'tools/call'; params = @{ name = 'close_windows'; arguments = @{} } }
)
$psi = New-Object Diagnostics.ProcessStartInfo $exe
$psi.UseShellExecute = $false; $psi.RedirectStandardInput = $true; $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true
$psi.StandardOutputEncoding = [Text.Encoding]::UTF8
$p = [Diagnostics.Process]::Start($psi)
$in = New-Object IO.StreamWriter($p.StandardInput.BaseStream, (New-Object Text.UTF8Encoding $false)); $in.AutoFlush = $true
foreach ($c in $calls) {
  $line = $c | ConvertTo-Json -Depth 10 -Compress
  $sw = [Diagnostics.Stopwatch]::StartNew()
  $in.WriteLine($line)
  if (-not $c.ContainsKey('id')) { continue }
  $resp = $p.StandardOutput.ReadLine()
  $label = if ($c.params.name) { "$($c.params.name) $($c.params.arguments | ConvertTo-Json -Compress)" } else { $c.method }
  if ($resp.Length -gt $Width) { $resp = $resp.Substring(0, $Width) + '...' }
  "[{0}] {1} ({2:N1}s)`n    {3}" -f $c.id, $label, $sw.Elapsed.TotalSeconds, $resp
}
$in.Close()
if (-not $p.WaitForExit(30000)) { 'server did not exit after stdin closed'; $p.Kill() }
"server exit code: $($p.ExitCode); RESPONSA processes left: $(@(Get-Process RESPONSA -EA SilentlyContinue).Count)"
$err = $p.StandardError.ReadToEnd(); if ($err) { "stderr: $err" }
