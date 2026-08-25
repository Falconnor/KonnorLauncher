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

function Get-Property($file, $key)
{

    $line = Get-Content $file |
    Where-Object {
        $_ -match "^$key="
    }

    if ($line) {
        return $line.Split("=",2)[1].Trim()
    }

    return $null
}


function Get-BooleanProperty($file, $key)
{

    $value = Get-Property $file $key

    return $value -eq "true"

}