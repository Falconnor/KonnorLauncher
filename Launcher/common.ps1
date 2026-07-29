function Get-Property($file, $key)
{

    $line = Get-Content $file |
    Where-Object {
        $_ -match "^$key="
    }


    if (!$line) {
        return $null
    }


    return ($line -split "=",2)[1]
}