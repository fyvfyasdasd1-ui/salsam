param(
    [Parameter(Mandatory = $true)][string] $Project
)
$ErrorActionPreference = 'Continue'
$output = & dotnet run --project $Project -c Release 2>&1
$code = $LASTEXITCODE
$output | ForEach-Object { Write-Output ([string] $_) }
if ($code -ne 0) {
    $detail = ($output | Select-Object -Last 18 | ForEach-Object { [string] $_ }) -join "`n"
    $detail = $detail.Replace('%', '%25').Replace("`r", '%0D').Replace("`n", '%0A')
    Write-Output "::error title=Check failed::$detail"
    exit $code
}
