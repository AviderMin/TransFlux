param([string]$ProcessName="TransFlux",[string]$Out="D:\tmp\uia-rects.txt",[int]$MaxDepth=12)
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$proc = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) { throw "Process not running" }
$el = [System.Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle)
$sb = New-Object System.Text.StringBuilder
$walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
function Walk($e,$depth,$path){
  if ($depth -gt $MaxDepth) { return }
  $ci = $e.Current
  $r = $ci.BoundingRectangle
  $ct = $ci.ControlType.ProgrammaticName -replace "ControlType\.",""
  if ($r.Width -gt 0 -and $r.Height -gt 0) {
    [void]$sb.AppendLine(("{0}|{1}|{2}|{3}|{4}|{5}|{6}|{7}" -f $depth,$path,$ct,($ci.Name -replace "[|\r\n]"," "),([int]$r.Left),([int]$r.Top),([int]$r.Width),([int]$r.Height)))
  }
  $i=0
  $child = $walker.GetFirstChild($e)
  while ($child -ne $null) { Walk $child ($depth+1) ($path + "." + $i); $i++; $child = $walker.GetNextSibling($child) }
}
Walk $el 0 "0"
Set-Content -Path $Out -Value $sb.ToString() -Encoding utf8
Write-Host ("Wrote " + $Out)
