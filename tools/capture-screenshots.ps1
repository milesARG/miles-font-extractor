# Captures the website/README screenshots from the real app, driving it with UI Automation.
# Uses only the demo folder (see make-demo-fonts.ps1) plus Windows' own and Program Files font folders,
# and presets the save folder, so no personal path or username ever appears on screen.
# Each PNG is a native 2x render of the window (MILES_SNAP_DIR hook), not a resized capture.
param(
    [string]$Exe = (Join-Path (Split-Path -Parent $PSScriptRoot) 'src\MilesFontExtractor\bin\Release\net8.0-windows\MilesFontExtractor.exe'),
    [string]$Out = (Join-Path (Split-Path -Parent $PSScriptRoot) 'website-assets\screenshots'),
    [string]$Demo = 'D:\Demo',
    [string[]]$Only = @()          # home, folders, scanning, flow (default: all)
)
function Want($n) { $Only.Count -eq 0 -or $Only -contains $n }
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$dest = Join-Path $Demo 'Extracted Fonts'
New-Item -ItemType Directory -Force $Out | Out-Null
$env:MILES_SNAP_DIR = $Out

function Stop-App { Get-Process MilesFontExtractor -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep 1 }
function Start-App([string[]]$AppArgs) {
    Stop-App
    $argLine = ($AppArgs | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }) -join ' '
    $script:proc = Start-Process $Exe -ArgumentList $argLine -PassThru
    Start-Sleep 4
    $script:proc.Refresh()
    $script:hwnd = $script:proc.MainWindowHandle   # cache it: the snapshot host adds a second top-level window later
}
function Root { $AE::FromHandle($script:hwnd) }
function Find($name) { (Root).FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $name))) }
function FindLike($ctrl, $pattern) {
    $all = (Root).FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $ctrl)))
    foreach ($e in $all) { if ($e.Current.Name -like $pattern) { return $e } }
}
function Click($name) { $e = Find $name; if (-not $e) { throw "not found: $name" }; $e.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function ClickLike($pattern) { $e = FindLike ([System.Windows.Automation.ControlType]::Button) $pattern; if (-not $e) { throw "no button like $pattern" }; $e.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function Wait($name, $sec) { $t = [DateTime]::Now.AddSeconds($sec); while ([DateTime]::Now -lt $t) { if (Find $name) { return $true }; Start-Sleep -Milliseconds 40 }; return $false }
function Snap($name) {
    $png = Join-Path $Out "$name.png"; Remove-Item $png -ErrorAction SilentlyContinue
    Set-Content (Join-Path $Out 'request.txt') $name
    $t = [DateTime]::Now.AddSeconds(15); while (-not (Test-Path $png) -and [DateTime]::Now -lt $t) { Start-Sleep -Milliseconds 100 }
    Start-Sleep -Milliseconds 200
    if (-not (Test-Path $png)) { throw "snapshot failed: $name" }
    "saved $name"
}
function SetSearch($text) {
    $edit = (Root).FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Edit)))
    $edit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($text)
}

# 01 - home
if (Want 'home') {
Start-App @('--dest', $dest)
Snap '01-home'
}

# 02 - home with two folders added and the drives unticked
if (Want 'folders') {
Start-App @('--dest', $dest, '--folder', (Join-Path $Demo 'Brand Pack'), '--folder', (Join-Path $Demo 'Client Work'))
Snap '02-home-folders-added'
}

# 03 - scanning: a big, impersonal folder set so the scan is still running when we capture
if (Want 'scanning') {
$env:MILES_SCAN_SLOWMO = '3'   # slow-motion so the scan is still running when captured
Start-App @('--dest', $dest, '--folder', 'C:\Windows', '--folder', 'C:\Program Files', '--folder', 'C:\Program Files (x86)')
Click 'Scan this PC'
$null = Wait 'Cancel' 10
Start-Sleep -Seconds 4
Snap '03-scanning'
Stop-App
Remove-Item Env:\MILES_SCAN_SLOWMO
}

# 04..10 - the full flow
if (Want 'flow') {
Start-App @('--dest', $dest, '--folder', 'C:\Windows\Fonts', '--folder', 'C:\Program Files', '--folder', 'C:\Program Files (x86)',
            '--folder', $Demo, '--folder', (Join-Path $env:LOCALAPPDATA 'Microsoft\Windows\Fonts'))
Click 'Scan this PC'
if (-not (Wait 'Extract fonts' 600)) { throw 'scan did not finish' }
Start-Sleep 2
Snap '04-review'

ClickLike '*Program fonts*'; Start-Sleep -Milliseconds 700
Snap '05-review-filtered'
ClickLike '*Loose fonts*'; Start-Sleep -Milliseconds 700

# 06 - preview: pick a font with a good specimen, then clear the search so the list is full again
SetSearch 'Constantia'; Start-Sleep -Milliseconds 600
$li = (Root).FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)))
$li.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
Start-Sleep -Milliseconds 600
SetSearch ''; Start-Sleep -Milliseconds 800
$sel = (Root).FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem))) |
    Where-Object { $_.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected } | Select-Object -First 1
if ($sel) { try { $sel.SetFocus() } catch { } }
Start-Sleep -Milliseconds 900
Snap '06-review-preview'

# 08 - copying (poll fast so the progress screen is caught mid-way)
Click 'Extract fonts'
$null = Wait 'Copying and verifying fonts' 10
Snap '08-copying'

# 09 - countdown
if (-not (Wait 'Remove now' 120)) { throw 'countdown not reached' }
Start-Sleep 1
Snap '09-countdown'

# 10 - done
Click 'Remove now'
if (-not (Wait 'Scan again' 120)) { throw 'done not reached' }
Start-Sleep 1
Snap '10-done'
Stop-App
}
"all screenshots captured"

# 07 - the bottom bar, cropped (native pixels, no resize) from the review shot
$review = Join-Path $Out '04-review.png'
if (Test-Path $review) {
    python -I -c "from PIL import Image; im=Image.open(r'$review'); w,h=im.size; im.crop((0,h-232,w,h)).save(r'$(Join-Path $Out '07-bottom-bar.png')')"
    "saved 07-bottom-bar"
}
