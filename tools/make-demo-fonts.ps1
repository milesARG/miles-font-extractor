# Builds a throw-away demo folder of fonts for screenshots: copies of fonts that ship with Windows,
# spread over realistic subfolders, with a few duplicates so de-duplication shows up.
#   make-demo-fonts.ps1 -Root D:\Demo
param([string]$Root = 'D:\Demo')
$win = "$env:WINDIR\Fonts"
$layout = [ordered]@{
    'Brand Pack'         = 'bahnschrift calibri calibrib calibril calibrii candara candarab candarai constan constanb corbel corbelb'
    'Client Work\Fonts'  = 'georgia georgiab georgiai pala palab framd trebuc trebucbd verdana verdanab tahoma tahomabd'
    'Free Fonts'         = 'segoeui segoeuil seguisb consola consolab cour comic impact times timesbd arial ariblk arialbd lucon'
    'Moodboard'          = 'gadugi ebrima javatext leelawui sylfaen nirmala malgun simsun msyh YuGothM msgothic seguisym'
    'Archive\Old Pitch'  = 'georgia calibri verdana candara constan'
}
New-Item -ItemType Directory -Force $Root | Out-Null
$n = 0
foreach ($dir in $layout.Keys) {
    $target = Join-Path $Root $dir
    New-Item -ItemType Directory -Force $target | Out-Null
    foreach ($name in $layout[$dir] -split ' ') {
        $src = Get-ChildItem $win -File | Where-Object { $_.BaseName -ieq $name -and $_.Extension -in '.ttf', '.otf', '.ttc' } | Select-Object -First 1
        if ($src) { Copy-Item $src.FullName (Join-Path $target $src.Name) -Force; (Get-Item (Join-Path $target $src.Name)).Attributes = 'Normal'; $n++ }
    }
}
"$n demo fonts in $Root"
