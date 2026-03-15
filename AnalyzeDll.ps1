$managed = "D:\Steam\steamapps\common\MIMESIS\MIMESIS_Data\Managed"
$dllPath = Join-Path $managed "Assembly-CSharp.dll"
Push-Location $managed
try {
    $asm = [System.Reflection.Assembly]::LoadFrom($dllPath)
    $binding = [System.Reflection.BindingFlags]::Public -bor [System.Reflection.BindingFlags]::NonPublic -bor [System.Reflection.BindingFlags]::Instance -bor [System.Reflection.BindingFlags]::Static -bor [System.Reflection.BindingFlags]::FlattenHierarchy
    $types = @()
    try { $types = $asm.GetTypes() } catch [System.Reflection.ReflectionTypeLoadException] { $types = $_.Types | Where-Object { $_ -ne $null } }
    Write-Output "=== Types with Vector3 fields ==="
    foreach ($t in $types) {
        try {
            foreach ($f in $t.GetFields($binding)) {
                $fn = $f.FieldType.FullName
                if ($fn -like "*Vector3*") {
                    Write-Output "V3: $($t.FullName) . $($f.Name)"
                }
            }
        } catch {}
    }
    Write-Output "=== Types with 'Transform' or 'position' in field type (Player/Character/Controller) ==="
    $keywords = @("Player", "Character", "Controller", "LocalPlayer", "GameManager", "Camera")
    foreach ($t in $types) {
        if ($t.FullName -notmatch "^\s*$") {
            foreach ($kw in $keywords) {
                if ($t.FullName -like "*$kw*") {
                    Write-Output "Type: $($t.FullName)"
                    try {
                        foreach ($f in $t.GetFields($binding)) {
                            Write-Output "  . $($f.Name) : $($f.FieldType.FullName)"
                        }
                    } catch {}
                    break
                }
            }
        }
    }
} finally { Pop-Location }
