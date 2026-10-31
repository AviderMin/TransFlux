param([string]$Name)
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$proc = Get-Process -Name TransFlux -ErrorAction SilentlyContinue | Select-Object -First 1
$el = [System.Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle)
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
$items = $el.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
$t=$null; foreach($i in $items){ if($i.Current.Name -eq $Name){ $t=$i; break } }
if(-not $t){ throw "button $Name not found" }
$p=$t.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern); $p.Invoke()
Start-Sleep -Milliseconds 1500
Write-Host "invoked $Name"
