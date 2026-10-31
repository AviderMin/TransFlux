param([string]$Name,[string]$Out)
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$proc = Get-Process -Name TransFlux -ErrorAction SilentlyContinue | Select-Object -First 1
$el = [System.Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle)
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
$items = $el.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
$target = $null
foreach($i in $items){ if($i.Current.Name -eq $Name){ $target=$i; break } }
if(-not $target){ throw "nav item $Name not found" }
$pat = $target.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
$pat.Select()
Start-Sleep -Milliseconds 1500
# dump rects
$sb = New-Object System.Text.StringBuilder
$walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
function Walk($e,$depth){ if($depth -gt 12){return}; $ci=$e.Current; $r=$ci.BoundingRectangle; $ct=$ci.ControlType.ProgrammaticName -replace "ControlType\.",""; if($r.Width -gt 0 -and $r.Height -gt 0){ [void]$sb.AppendLine(("{0}|{1}|{2}|{3}|{4}|{5}|{6}" -f $depth,$ct,($ci.Name -replace "[|\r\n]"," "),[int]$r.Left,[int]$r.Top,[int]$r.Width,[int]$r.Height)) }; $c=$walker.GetFirstChild($e); while($c -ne $null){ Walk $c ($depth+1); $c=$walker.GetNextSibling($c) } }
Walk $el 0
Set-Content -Path $Out -Value $sb.ToString() -Encoding utf8
Write-Host "dumped $Out"
