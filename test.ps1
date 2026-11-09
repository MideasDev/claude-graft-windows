# The suite. Everything it does happens in a throwaway folder under %TEMP%;
# it never opens, closes or reads a real Claude.
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
dotnet run --project tests\ClaudeGraft.Tests -c Release
exit $LASTEXITCODE
