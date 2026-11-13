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
  @{ jsonrpc = '2.0'; id = 13; method = 'tools/call'; params = @{ name = 'close_windows'; arguments = @{} } },
  # IT-789: books scope. Expect: Daniel only, 3 results incl. 11:41; then an unscoped search restarts the instance
  # (note) and searches all databases again; ambiguous / unknown names are errors with candidates; a commentator path.
  @{ jsonrpc = '2.0'; id = 14; method = 'tools/call'; params = @{ name = 'search'; arguments = @{ query = '#(אדום/עשו)'; books = @('דניאל'); limit = 5 } } },
  @{ jsonrpc = '2.0'; id = 140; method = 'tools/call'; params = @{ name = 'get_results'; arguments = @{ search_id = 2; from = 3; count = 1 } } },
  @{ jsonrpc = '2.0'; id = 141; method = 'tools/call'; params = @{ name = 'open_text'; arguments = @{ reference = 'בבלי ברכות ב א'; max_chars = 500 } } },
  @{ jsonrpc = '2.0'; id = 15; method = 'tools/call'; params = @{ name = 'search'; arguments = @{ query = '#מקוה [1:3] #טהרה'; all_databases = $false; limit = 1 } } },
  @{ jsonrpc = '2.0'; id = 16; method = 'tools/call'; params = @{ name = 'search'; arguments = @{ query = '#(אדום/עשו)'; books = @('רש"י') } } },
  @{ jsonrpc = '2.0'; id = 17; method = 'tools/call'; params = @{ name = 'search'; arguments = @{ query = '#(אדום/עשו)'; books = @('ספר שאינו קיים') } } },
  @{ jsonrpc = '2.0'; id = 18; method = 'tools/call'; params = @{ name = 'search'; arguments = @{ query = '#(אדום/עשו)'; books = @('דניאל'); all_databases = $true } } },
  @{ jsonrpc = '2.0'; id = 19; method = 'tools/call'; params = @{ name = 'search'; arguments = @{ query = '#מלך'; books = @('רש"י ומפרשיו / רש"י / דניאל', 'מצודת דוד', 'אסתר'); limit = 2 } } },
  @{ jsonrpc = '2.0'; id = 20; method = 'tools/call'; params = @{ name = 'search'; arguments = @{ query = '#מקוה [1:3] #טהרה'; all_databases = $true; limit = 1 } } },
  @{ jsonrpc = '2.0'; id = 21; method = 'tools/call'; params = @{ name = 'search'; arguments = @{ query = '#מקוה [1:3] #טהרה'; limit = 1 } } }
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
