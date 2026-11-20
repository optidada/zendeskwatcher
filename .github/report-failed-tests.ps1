# Turns each failed test in the .trx results under the given folder into a GitHub annotation (name + message + the
# first lines of the stack trace), so the failure can be read without opening the log.
param([string]$Folder = "TestResults")
$files = Get-ChildItem -Path $Folder -Filter *.trx -Recurse -ErrorAction SilentlyContinue
if (-not $files) { Write-Output "::error title=Tests::No test results found (the build may have failed)"; exit 0 }
foreach ($file in $files) {
    [xml]$trx = Get-Content $file.FullName -Raw
    foreach ($result in $trx.TestRun.Results.UnitTestResult | Where-Object { $_.outcome -eq "Failed" }) {
        $message = "$($result.Output.ErrorInfo.Message)"
        $stack = (("$($result.Output.ErrorInfo.StackTrace)" -split "`n") | Select-Object -First 4) -join "`n"
        $text = "$message`n$stack"
        if ($text.Length -gt 3000) { $text = $text.Substring(0, 3000) }
        $text = $text.Replace("%", "%25").Replace("`r", "").Replace("`n", "%0A")
        Write-Output "::error title=Failed: $($result.testName)::$text"
    }
}
