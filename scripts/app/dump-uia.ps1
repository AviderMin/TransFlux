param(
  [string]$ProcessName = 'TransFlux',
  [string]$Out = 'artifacts/evidence/ui-tree.txt',
  [int]$MaxDepth = 9
)
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$path = Join-Path $root $Out
New-Item -ItemType Directory -Force (Split-Path $path) | Out-Null
$proc = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) { throw "Process $ProcessName not running." }
$el = [System.Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle)
$sb = New-Object System.Text.StringBuilder
$walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
function Walk($e, $depth) {
  if ($depth -gt $MaxDepth) { return }
  $ci = $e.Current
  $indent = '  ' * $depth
  $ct = $ci.ControlType.ProgrammaticName -replace 'ControlType\.', ''
  [void]$sb.AppendLine("$indent$ct name='$($ci.Name)' autoId='$($ci.AutomationId)'")
  $child = $walker.GetFirstChild($e)
  while ($child -ne $null) { Walk $child ($depth + 1); $child = $walker.GetNextSibling($child) }
}
Walk $el 0
Set-Content -Path $path -Value $sb.ToString() -Encoding utf8
Write-Host "Wrote UI tree to $path"
