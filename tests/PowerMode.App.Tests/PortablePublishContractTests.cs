using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Xunit;

namespace PowerModeWinUI.Tests;

public sealed class PortablePublishContractTests
{
    [Fact]
    public async Task PublishScript_DefaultRunsGuardedTestsBeforePublish()
    {
        var result = await PowerShellAstAssertions.AnalyzePublishScriptAsync(
            TestPaths.Repo("scripts", "Publish-Portable.ps1"));

        Assert.True(result.TestGuardedBySkipTests);
        Assert.True(result.TestPrecedesPublish);
        Assert.True(result.TestChecksLastExitCode);
        Assert.True(result.PublishChecksLastExitCode);
        Assert.True(result.TestCommandContract);
        Assert.True(result.PublishCommandContract);
        Assert.True(result.PathsUseResolvedRoot);
    }

    [Fact]
    public async Task PublishScript_DeclaresDiagnosticSkipAndCopiesHashedPortableInputs()
    {
        var result = await PowerShellAstAssertions.AnalyzePublishScriptAsync(
            TestPaths.Repo("scripts", "Publish-Portable.ps1"));

        Assert.True(result.HasExplicitSkipTestsParameter);
        Assert.True(result.CopiesCliEngineAndReadme);
        Assert.True(result.HashesCliEngineAndReadme);
    }

    [Fact]
    public async Task PublishScript_DisablesDebugSymbolsAndRejectsDevelopmentArtifacts()
    {
        var result = await PowerShellAstAssertions.AnalyzePublishScriptAsync(
            TestPaths.Repo("scripts", "Publish-Portable.ps1"));

        Assert.True(result.DisablesDebugSymbols);
        Assert.True(result.RejectsDebugAndTestArtifacts);
    }

    [Fact]
    public async Task PublishScript_WritesVersionedProvenanceAndZipChecksum()
    {
        var result = await PowerShellAstAssertions.AnalyzePublishScriptAsync(
            TestPaths.Repo("scripts", "Publish-Portable.ps1"));

        Assert.True(result.ReadsExecutableProductVersion);
        Assert.True(result.BuildInfoContainsRequiredFields);
        Assert.True(result.CreateZipWritesSha256Sidecar);
    }
}

internal sealed record PortablePublishContract(
    bool TestGuardedBySkipTests,
    bool TestPrecedesPublish,
    bool TestChecksLastExitCode,
    bool PublishChecksLastExitCode,
    bool TestCommandContract,
    bool PublishCommandContract,
    bool PathsUseResolvedRoot,
    bool HasExplicitSkipTestsParameter,
    bool CopiesCliEngineAndReadme,
    bool HashesCliEngineAndReadme,
    bool DisablesDebugSymbols,
    bool RejectsDebugAndTestArtifacts,
    bool ReadsExecutableProductVersion,
    bool BuildInfoContainsRequiredFields,
    bool CreateZipWritesSha256Sidecar);

internal static class PowerShellAstAssertions
{
    public static async Task<PortablePublishContract> AnalyzePublishScriptAsync(
        string scriptPath)
    {
        var literalPath = scriptPath.Replace("'", "''", StringComparison.Ordinal);
        var command = $$"""
            $path = '{{literalPath}}'
            $tokens = $null
            $parseErrors = $null
            $root = [System.Management.Automation.Language.Parser]::ParseFile(
                $path,
                [ref]$tokens,
                [ref]$parseErrors)
            if ($null -ne $parseErrors -and $parseErrors.Count -gt 0) {
                throw ($parseErrors | ForEach-Object { $_.Message } | Out-String)
            }

            $commandType = [System.Management.Automation.Language.CommandAst]
            $pipelineType = [System.Management.Automation.Language.PipelineAst]
            $ifType = [System.Management.Automation.Language.IfStatementAst]
            $statementBlockType = [System.Management.Automation.Language.StatementBlockAst]
            $namedBlockType = [System.Management.Automation.Language.NamedBlockAst]
            $variableType = [System.Management.Automation.Language.VariableExpressionAst]
            $stringType = [System.Management.Automation.Language.StringConstantExpressionAst]
            $assignmentType = [System.Management.Automation.Language.AssignmentStatementAst]
            $parameterType = [System.Management.Automation.Language.ParameterAst]
            $throwType = [System.Management.Automation.Language.ThrowStatementAst]
            $commands = @($root.FindAll({ param($node) $node -is $commandType }, $true))
            $ifs = @($root.FindAll({ param($node) $node -is $ifType }, $true))
            $assignments = @($root.FindAll({ param($node) $node -is $assignmentType }, $true))
            $parameters = @($root.FindAll({ param($node) $node -is $parameterType }, $true))

            function Get-Text($node) {
                if ($node -is $stringType) {
                    return $node.Value
                }
                if ($node -is [System.Management.Automation.Language.CommandParameterAst]) {
                    return $node.Extent.Text
                }
                return $null
            }

            function Get-CommandParts($commandAst) {
                $parts = @()
                foreach ($element in @($commandAst.CommandElements)) {
                    $text = Get-Text $element
                    if ($null -ne $text) {
                        $parts += $text
                    }
                }
                return $parts
            }

            function Is-Command($commandAst, [string]$name) {
                $parts = @(Get-CommandParts $commandAst)
                return $parts.Count -gt 0 -and
                    $parts[0].Equals($name, [System.StringComparison]::OrdinalIgnoreCase)
            }

            function Has-CommandToken($commandAst, [string]$token) {
                return @(Get-CommandParts $commandAst | Where-Object {
                    $_.Equals($token, [System.StringComparison]::OrdinalIgnoreCase)
                }).Count -gt 0
            }

            function Is-NativeCommand($commandAst, [string]$verb) {
                $parts = @(Get-CommandParts $commandAst)
                return $parts.Count -ge 2 -and
                    $parts[0].Equals('dotnet', [System.StringComparison]::OrdinalIgnoreCase) -and
                    $parts[1].Equals($verb, [System.StringComparison]::OrdinalIgnoreCase)
            }

            function Unwrap-Condition($condition) {
                while ($true) {
                    if ($condition -is [System.Management.Automation.Language.ParenExpressionAst]) {
                        $condition = $condition.Pipeline
                        continue
                    }
                    if ($condition -is [System.Management.Automation.Language.PipelineAst] -and
                        $condition.PipelineElements.Count -eq 1) {
                        $condition = $condition.PipelineElements[0]
                        if ($condition -is [System.Management.Automation.Language.CommandExpressionAst]) {
                            $condition = $condition.Expression
                        }
                        continue
                    }
                    break
                }
                return $condition
            }

            function Is-VariableCondition($condition, [string]$name) {
                $condition = Unwrap-Condition $condition
                if ($condition -is [System.Management.Automation.Language.UnaryExpressionAst] -and
                    $condition.TokenKind.ToString().Equals('Not', [System.StringComparison]::OrdinalIgnoreCase)) {
                    return Is-VariableCondition $condition.Child $name
                }
                return $condition -is $variableType -and
                    $condition.VariablePath.UserPath.Equals($name, [System.StringComparison]::OrdinalIgnoreCase)
            }

            function Get-ClauseBodies($ifAst, [scriptblock]$predicate) {
                $bodies = @()
                foreach ($clause in @($ifAst.Clauses)) {
                    if (& $predicate $clause.Item1) {
                        $bodies += $clause.Item2
                    }
                }
                return $bodies
            }

            function Contains-Native($node, [string]$verb) {
                return @($node.FindAll({ param($candidate)
                    $candidate -is $commandType -and $(Is-NativeCommand $candidate $verb)
                }, $true)).Count -gt 0
            }

            function Get-ContainingStatement($node) {
                $current = $node
                while ($null -ne $current.Parent -and
                    $current.Parent -isnot $statementBlockType -and
                    $current.Parent -isnot $namedBlockType) {
                    $current = $current.Parent
                }
                if ($null -ne $current.Parent -and
                    ($current.Parent -is $statementBlockType -or
                        $current.Parent -is $namedBlockType)) {
                    return $current
                }
                return $null
            }

            function Has-LastExitCodeThrowGuard($node) {
                $statement = Get-ContainingStatement $node
                if ($null -eq $statement) {
                    return $false
                }
                $block = $statement.Parent
                $statements = @($block.Statements)
                for ($index = 0; $index -lt $statements.Count - 1; $index++) {
                    if ($statements[$index].Extent.StartOffset -ne $statement.Extent.StartOffset) {
                        continue
                    }
                    $next = $statements[$index + 1]
                    if ($next -isnot $ifType) {
                        return $false
                    }
                    $conditionText = (@($next.Clauses | ForEach-Object { $_.Item1.Extent.Text }) -join ' ')
                    $hasLastExitCode = $conditionText -match '\$LASTEXITCODE\s+-ne\s+0'
                    $hasThrow = @($next.FindAll({ param($candidate) $candidate -is $throwType }, $true)).Count -gt 0
                    return $hasLastExitCode -and $hasThrow
                }
                return $false
            }

            function Has-Variable($node, [string]$name) {
                return @($node.FindAll({ param($candidate)
                    $candidate -is $variableType -and
                        $candidate.VariablePath.UserPath.Equals($name, [System.StringComparison]::OrdinalIgnoreCase)
                }, $true)).Count -gt 0
            }

            function Assignment-Name($assignment) {
                if ($assignment.Left -is $variableType) {
                    return $assignment.Left.VariablePath.UserPath
                }
                return $null
            }

            function Assignment-UsesResolvedRoot($assignment) {
                return @($assignment.Right.FindAll({ param($candidate)
                        $candidate -is $commandType -and
                        (@(Get-CommandParts $candidate).Count -gt 0) -and
                        (@(Get-CommandParts $candidate)[0]).Equals('Join-Path', [System.StringComparison]::OrdinalIgnoreCase) -and
                        (@('rootPath', 'dist', 'staging') | Where-Object {
                            Has-Variable $candidate $_
                        }).Count -gt 0
                }, $true)).Count -gt 0
            }

            $testCommand = @($commands | Where-Object { Is-NativeCommand $_ 'test' } | Sort-Object { $_.Extent.StartOffset }) | Select-Object -First 1
            $publishCommand = @($commands | Where-Object { Is-NativeCommand $_ 'publish' } | Sort-Object { $_.Extent.StartOffset }) | Select-Object -First 1
            $testIfBody = $null
            foreach ($ifAst in $ifs) {
                foreach ($body in @(Get-ClauseBodies $ifAst { param($condition) Is-VariableCondition $condition 'SkipTests' })) {
                    if (Contains-Native $body 'test') {
                        $testIfBody = $body
                        break
                    }
                }
                if ($null -ne $testIfBody) { break }
            }

            $rootPathAssignment = @($assignments | Where-Object { (Assignment-Name $_).Equals('rootPath', [System.StringComparison]::OrdinalIgnoreCase) }) | Select-Object -First 1
            $rootPathIsResolved = $null -ne $rootPathAssignment -and
                @($rootPathAssignment.Right.FindAll({ param($candidate)
                    $candidate -is [System.Management.Automation.Language.InvokeMemberExpressionAst] -and
                        $candidate.Member.Value.Equals('GetFullPath', [System.StringComparison]::OrdinalIgnoreCase)
                }, $true)).Count -gt 0
            $rootDerivedAssignments = @($assignments | Where-Object {
                (Assignment-Name $_) -in @('solution', 'project', 'dist', 'output', 'appOutput') -and
                (Assignment-UsesResolvedRoot $_)
            })

            $hasSkipTestsParameter = @($parameters | Where-Object {
                $_.Name.VariablePath.UserPath.Equals('SkipTests', [System.StringComparison]::OrdinalIgnoreCase) -and
                @($_.Attributes | Where-Object {
                    $_ -is [System.Management.Automation.Language.TypeConstraintAst] -and
                    $_.TypeName.Name.Equals('switch', [System.StringComparison]::OrdinalIgnoreCase)
                }).Count -gt 0
            }).Count -gt 0

            $copyCommands = @($commands | Where-Object {
                $parts = @(Get-CommandParts $_)
                $parts.Count -gt 0 -and $parts[0].Equals('Copy-Item', [System.StringComparison]::OrdinalIgnoreCase)
            })
            $hashCommands = @($commands | Where-Object {
                $parts = @(Get-CommandParts $_)
                $parts.Count -gt 0 -and $parts[0].Equals('Get-FileHash', [System.StringComparison]::OrdinalIgnoreCase)
            })
            $copySources = @('cliSource', 'engineSource', 'readmeSource') | Where-Object {
                $sourceName = $_
                @($copyCommands | Where-Object { Has-Variable $_ $sourceName }).Count -gt 0
            }
            $hashedSources = @('cliSource', 'engineSource', 'readmeSource') | Where-Object {
                $sourceName = $_
                @($hashCommands | Where-Object { Has-Variable $_ $sourceName }).Count -gt 0
            }

            $publishParts = if ($null -ne $publishCommand) { @(Get-CommandParts $publishCommand) } else { @() }
            $testCommandContract = $null -ne $testCommand -and
                (Has-Variable $testCommand 'solution') -and
                (Has-CommandToken $testCommand '-c') -and
                (Has-CommandToken $testCommand 'Release') -and
                (Has-CommandToken $testCommand '-p:Platform=x64') -and
                (Has-CommandToken $testCommand '-m:1') -and
                (Has-CommandToken $testCommand '-p:UseSharedCompilation=false')
            $publishCommandContract = $null -ne $publishCommand -and
                (Has-Variable $publishCommand 'project') -and
                (Has-Variable $publishCommand 'appOutput') -and
                (Has-CommandToken $publishCommand '-c') -and
                (Has-CommandToken $publishCommand 'Release') -and
                (Has-CommandToken $publishCommand '-r') -and
                (Has-CommandToken $publishCommand 'win-x64') -and
                (Has-CommandToken $publishCommand '--self-contained') -and
                (Has-CommandToken $publishCommand 'true') -and
                (Has-CommandToken $publishCommand '-p:Platform=x64') -and
                (Has-CommandToken $publishCommand '-p:DebugType=None') -and
                (Has-CommandToken $publishCommand '-p:DebugSymbols=false') -and
                (Has-CommandToken $publishCommand '-o')
            $debugDisabled = $publishParts -contains '-p:DebugType=None' -and
                $publishParts -contains '-p:DebugSymbols=false'

            $copyContracts = @(
                @{ Source = 'cliSource'; Destination = 'cliDestination' },
                @{ Source = 'engineSource'; Destination = 'engineDestination' },
                @{ Source = 'readmeSource'; Destination = 'readmeDestination' })
            $copiesAreBound = @($copyContracts | Where-Object {
                $contract = $_
                @($copyCommands | Where-Object {
                    (Has-Variable $_ $contract.Source) -and
                    (Has-Variable $_ $contract.Destination)
                }).Count -gt 0
            }).Count -eq $copyContracts.Count

            $hashContracts = @(
                @{ Source = 'cliSource'; Destination = 'cliDestination'; SourceHash = 'sourceCliHash'; DestinationHash = 'publishedCliHash' },
                @{ Source = 'engineSource'; Destination = 'engineDestination'; SourceHash = 'sourceEngineHash'; DestinationHash = 'publishedEngineHash' },
                @{ Source = 'readmeSource'; Destination = 'readmeDestination'; SourceHash = 'sourceReadmeHash'; DestinationHash = 'publishedReadmeHash' })
            $hashesAreBound = @($hashContracts | Where-Object {
                $contract = $_
                @($hashCommands | Where-Object { Has-Variable $_ $contract.Source }).Count -gt 0 -and
                @($hashCommands | Where-Object { Has-Variable $_ $contract.Destination }).Count -gt 0
            }).Count -eq $hashContracts.Count
            $hashComparisonsAreBound = @($hashContracts | Where-Object {
                $contract = $_
                @($ifs | Where-Object {
                    $_.Extent.Text -match '\-ne' -and
                    (Has-Variable $_ $contract.SourceHash) -and
                    (Has-Variable $_ $contract.DestinationHash)
                }).Count -gt 0
            }).Count -eq $hashContracts.Count

            $artifactFilter = @($commands | Where-Object {
                $parts = @(Get-CommandParts $_)
                $parts.Count -gt 0 -and $parts[0].Equals('Where-Object', [System.StringComparison]::OrdinalIgnoreCase) -and
                @($_.FindAll({ param($candidate)
                    $candidate -is $stringType -and $candidate.Value -in @('*.pdb', '*Tests*.dll', 'obj', 'ref', 'refint')
                }, $true)).Count -ge 5
            }).Count -gt 0
            $hasArtifactPipeline = @($root.FindAll({ param($candidate)
                if ($candidate -isnot $pipelineType) {
                    return $false
                }
                $hasChildFileScan = @($candidate.FindAll({ param($nested)
                    $nested -is $commandType -and
                    (Is-Command $nested 'Get-ChildItem') -and
                    (Has-Variable $nested 'appOutput') -and
                    (Has-CommandToken $nested '-LiteralPath') -and
                    (Has-CommandToken $nested '-Recurse') -and
                    (Has-CommandToken $nested '-File')
                }, $true)).Count -gt 0
                $hasArtifactFilter = @($candidate.FindAll({ param($nested)
                    $nested -is $commandType -and
                    (Is-Command $nested 'Where-Object') -and
                    @($nested.FindAll({ param($literal)
                        $literal -is $stringType -and $literal.Value -in @('*.pdb', '*Tests*.dll', 'obj', 'ref', 'refint')
                    }, $true)).Count -ge 5
                }, $true)).Count -gt 0
                return $hasChildFileScan -and $hasArtifactFilter
            }, $true)).Count -gt 0

            $productVersionAssignment = @($assignments | Where-Object {
                (Assignment-Name $_).Equals('productVersion', [System.StringComparison]::OrdinalIgnoreCase) -and
                (Has-Variable $_ 'executableItem') -and
                @($_.FindAll({ param($candidate)
                    $candidate -is [System.Management.Automation.Language.MemberExpressionAst] -and
                        $candidate.Member.Value.Equals('ProductVersion', [System.StringComparison]::OrdinalIgnoreCase)
                }, $true)).Count -gt 0
            }) | Select-Object -First 1
            $productVersionRead = @($commands | Where-Object {
                (Is-Command $_ 'Get-Item') -and (Has-Variable $_ 'executable')
            }).Count -gt 0 -and $null -ne $productVersionAssignment

            $requiredBuildInfoKeys = @('version', 'commit', 'dirty', 'builtAtUtc', 'executable', 'runtime', 'selfContained', 'executableSha256', 'engineSha256', 'cliSha256', 'readmeSha256', 'publishScriptSha256', 'productVersion')
            $buildInfoKeys = @($root.FindAll({ param($candidate)
                $candidate -is [System.Management.Automation.Language.StringConstantExpressionAst] -and
                    $candidate.Value -in $requiredBuildInfoKeys
            }, $true) | ForEach-Object { $_.Value } | Select-Object -Unique)
            $buildInfoWrite = @($root.FindAll({ param($candidate)
                $candidate -is [System.Management.Automation.Language.InvokeMemberExpressionAst] -and
                $candidate.Member.Value.Equals('WriteAllText', [System.StringComparison]::OrdinalIgnoreCase) -and
                (Has-Variable $candidate 'staging') -and
                (Has-Variable $candidate 'buildInfo')
            }, $true)).Count -gt 0
            $hasBuildInfo = @($requiredBuildInfoKeys | Where-Object { $buildInfoKeys -contains $_ }).Count -eq $requiredBuildInfoKeys.Count -and
                $buildInfoWrite

            $createZipSidecar = $false
            foreach ($ifAst in $ifs) {
                foreach ($body in @(Get-ClauseBodies $ifAst { param($condition) Is-VariableCondition $condition 'CreateZip' })) {
                    $hasZipArchive = @($body.FindAll({ param($candidate)
                        $candidate -is $commandType -and
                        (Is-Command $candidate 'Compress-Archive') -and
                        (Has-Variable $candidate 'stagingZip')
                    }, $true)).Count -gt 0
                    $hasZipHash = @($body.FindAll({ param($candidate)
                        $candidate -is $commandType -and
                        (Is-Command $candidate 'Get-FileHash') -and
                        (Has-Variable $candidate 'stagingZip')
                    }, $true)).Count -gt 0 -and (Has-Variable $body 'zipHash')
                    $hasSidecarWrite = @($body.FindAll({ param($candidate)
                        $candidate -is [System.Management.Automation.Language.InvokeMemberExpressionAst] -and
                        $candidate.Member.Value.Equals('WriteAllText', [System.StringComparison]::OrdinalIgnoreCase) -and
                        (Has-Variable $candidate 'stagingZipSidecar') -and
                        (Has-Variable $candidate 'zipHash')
                    }, $true)).Count -gt 0
                    if ($hasZipArchive -and $hasZipHash -and $hasSidecarWrite) {
                        $createZipSidecar = $true
                        break
                    }
                }
                if ($createZipSidecar) { break }
            }

            [ordered]@{
                TestGuardedBySkipTests = $null -ne $testIfBody
                TestPrecedesPublish = $null -ne $testCommand -and $null -ne $publishCommand -and $testCommand.Extent.StartOffset -lt $publishCommand.Extent.StartOffset
                TestChecksLastExitCode = $null -ne $testCommand -and (Has-LastExitCodeThrowGuard $testCommand)
                PublishChecksLastExitCode = $null -ne $publishCommand -and (Has-LastExitCodeThrowGuard $publishCommand)
                TestCommandContract = $testCommandContract
                PublishCommandContract = $publishCommandContract
                PathsUseResolvedRoot = $rootPathIsResolved -and $rootDerivedAssignments.Count -ge 5 -and
                    $null -ne $testCommand -and (Has-Variable $testCommand 'solution') -and
                    $null -ne $publishCommand -and (Has-Variable $publishCommand 'project')
                HasExplicitSkipTestsParameter = $hasSkipTestsParameter
                CopiesCliEngineAndReadme = $copySources.Count -eq 3 -and $copiesAreBound
                HashesCliEngineAndReadme = $hashedSources.Count -eq 3 -and $hashesAreBound -and $hashComparisonsAreBound
                DisablesDebugSymbols = $debugDisabled
                RejectsDebugAndTestArtifacts = $artifactFilter -and $hasArtifactPipeline
                ReadsExecutableProductVersion = $productVersionRead
                BuildInfoContainsRequiredFields = $hasBuildInfo
                CreateZipWritesSha256Sidecar = $createZipSidecar
            } | ConvertTo-Json -Compress
            """;

        var helperPath = Path.Combine(
            Path.GetTempPath(),
            $"powermode-publish-ast-{Guid.NewGuid():N}.ps1");
        await File.WriteAllTextAsync(helperPath, command, Encoding.Unicode);
        try
        {
            var invocation = $"& '{helperPath.Replace("'", "''", StringComparison.Ordinal)}'";
            var encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(invocation));
            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("-NoLogo");
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-EncodedCommand");
            startInfo.ArgumentList.Add(encodedCommand);

            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                throw new InvalidOperationException("Unable to start bounded PowerShell AST helper.");
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
                await Task.WhenAll(outputTask, errorTask);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }

                throw new TimeoutException("PowerShell AST helper exceeded its 15 second bound.");
            }

            var output = outputTask.Result;
            var error = errorTask.Result;
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"PowerShell AST helper failed with exit code {process.ExitCode}: {error}");
            }

            return JsonSerializer.Deserialize<PortablePublishContract>(
                       output.Trim(),
                       new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                   ?? throw new InvalidOperationException("PowerShell AST helper returned no JSON result.");
        }
        finally
        {
            try
            {
                File.Delete(helperPath);
            }
            catch (IOException)
            {
            }
        }
    }
}
