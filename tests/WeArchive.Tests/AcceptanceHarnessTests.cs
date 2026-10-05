using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Xunit;

namespace WeArchive.Tests;

/// <summary>
/// Tests the Issue #86 acceptance harness logic (acceptance/acceptance-harness.psm1):
/// point classification, the no-change expectation, evidence-record schema validation,
/// idempotent trace writes, missed-point accounting and privacy-safe path rendering.
/// <para>
/// The harness is PowerShell by design (it drives the official RC binary), so these
/// tests import the module in a short-lived pwsh process and assert on its
/// machine-readable JSON output. They skip deterministically when the harness module
/// is not adjacent to the repository checkout or when pwsh is not installed (the
/// harness itself is inoperative without pwsh; CI runs on windows-latest where it is
/// preinstalled).
/// </para>
/// </summary>
public sealed class AcceptanceHarnessTests
{
    private static readonly string? HarnessModulePath = FindHarnessModule();
    private static readonly string? PwshPath = HarnessModulePath is null ? null : FindPwsh();

    private sealed class PwshFactAttribute : FactAttribute
    {
        public PwshFactAttribute()
        {
            if (HarnessModulePath is null)
            {
                Skip = "acceptance/acceptance-harness.psm1 not found relative to the test assembly";
            }
            else if (PwshPath is null)
            {
                Skip = "pwsh (PowerShell 7) is not available on PATH";
            }
        }
    }

    private static string? FindHarnessModule()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "acceptance", "acceptance-harness.psm1");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static string? FindPwsh()
    {
        var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in pathVariable.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory.Trim(), "pwsh.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Evaluates a PowerShell expression with the harness module imported and returns
    /// the <c>value</c> property of its JSON envelope, so string, boolean and null
    /// results all survive the round-trip.
    /// </summary>
    private static JsonElement EvalValue(string expression)
    {
        var stdout = RunHarness(
            "$script:Result = " + expression + "\n" +
            "@{ value = $script:Result } | ConvertTo-Json -Depth 16 -Compress");
        using var document = JsonDocument.Parse(stdout);
        return document.RootElement.Clone().GetProperty("value");
    }

    /// <summary>Runs a PowerShell body with the harness module imported; returns stdout.</summary>
    private static string RunHarness(string body)
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(
            "Import-Module '" + HarnessModulePath!.Replace("'", "''") + "' -Force\n" + body));

        var startInfo = new ProcessStartInfo
        {
            FileName = PwshPath!,
            Arguments = "-NoProfile -NonInteractive -EncodedCommand " + encoded,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var process = Process.Start(startInfo)!;
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync().GetAwaiter().GetResult();
        var stdout = stdoutTask.GetAwaiter().GetResult();
        process.WaitForExit(120_000);

        if (process.ExitCode != 0)
        {
            Assert.Fail("Harness snippet failed (exit " + process.ExitCode + "): " + stderr);
        }
        return stdout;
    }

    /// <summary>
    /// Evaluates a PowerShell expression that emits a hashtable and deserializes it.
    /// </summary>
    private static JsonElement EvalObject(string expression)
    {
        var stdout = RunHarness(expression + "\n$script:Result | ConvertTo-Json -Depth 16 -Compress");
        using var document = JsonDocument.Parse(stdout);
        return document.RootElement.Clone();
    }

    // ------------------------------------------------------------------
    // Point classification (every planned point must be classifiable)
    // ------------------------------------------------------------------

    [PwshFact]
    public void SuccessfulCaptureWithPassedVerifyClassifiesSuccess()
    {
        var outcome = EvalValue(
            "Get-AcceptancePointOutcome -CaptureExitCode 0 -CaptureJson ([pscustomobject]@{completeness='complete'}) " +
            "-VerifyExitCode 0 -VerifyJson ([pscustomobject]@{succeeded=$true})");
        Assert.Equal("success", outcome.GetString());
    }

    [PwshFact]
    public void PublishedPartialGenerationClassifiesSuccessPartial()
    {
        var outcome = EvalValue(
            "Get-AcceptancePointOutcome -CaptureExitCode 0 -CaptureJson ([pscustomobject]@{completeness='partial'}) " +
            "-VerifyExitCode 0 -VerifyJson ([pscustomobject]@{succeeded=$true})");
        Assert.Equal("success_partial", outcome.GetString());
    }

    [PwshFact]
    public void FailedCaptureClassifiesFailedCapture()
    {
        var outcome = EvalValue(
            "Get-AcceptancePointOutcome -CaptureExitCode 1 -CaptureJson ([pscustomobject]@{error=[pscustomobject]@{code='source_unavailable'}}) " +
            "-VerifyExitCode 0 -VerifyJson $null");
        Assert.Equal("failed_capture", outcome.GetString());
    }

    [PwshFact]
    public void FailedVerifyAfterPublishedCaptureClassifiesFailedVerify()
    {
        var outcome = EvalValue(
            "Get-AcceptancePointOutcome -CaptureExitCode 0 -CaptureJson ([pscustomobject]@{completeness='complete'}) " +
            "-VerifyExitCode 1 -VerifyJson ([pscustomobject]@{succeeded=$false})");
        Assert.Equal("failed_verify", outcome.GetString());
    }

    // ------------------------------------------------------------------
    // No-change hour expectation (zero new payload for unchanged artifacts)
    // ------------------------------------------------------------------

    [PwshFact]
    public void FullyReusedCaptureWithZeroGrowthMeetsNoChangeExpectation()
    {
        var result = EvalValue(
            "Test-AcceptanceNoChangeExpectation -CaptureJson ([pscustomobject]@{" +
            "coverage_summary=[pscustomobject]@{expected=5;captured=0;reused=5;unavailable=0;unsupported=0};" +
            "storage_counters=[pscustomobject]@{new_data_bytes=0;new_map_nodes=0}})");
        Assert.True(result.GetBoolean());
    }

    [PwshFact]
    public void FullyReusedCaptureWithNewDataViolatesNoChangeExpectation()
    {
        var result = EvalValue(
            "Test-AcceptanceNoChangeExpectation -CaptureJson ([pscustomobject]@{" +
            "coverage_summary=[pscustomobject]@{expected=5;captured=0;reused=5;unavailable=0;unsupported=0};" +
            "storage_counters=[pscustomobject]@{new_data_bytes=4096;new_map_nodes=0}})");
        Assert.False(result.GetBoolean());
    }

    [PwshFact]
    public void CaptureWithCapturedPartitionsIsNotANoChangeCapture()
    {
        var result = EvalValue(
            "Test-AcceptanceNoChangeExpectation -CaptureJson ([pscustomobject]@{" +
            "coverage_summary=[pscustomobject]@{expected=5;captured=1;reused=4;unavailable=0;unsupported=0};" +
            "storage_counters=[pscustomobject]@{new_data_bytes=4096;new_map_nodes=2}})");
        Assert.Equal(JsonValueKind.Null, result.ValueKind);
    }

    // ------------------------------------------------------------------
    // Evidence records (schema + explanation requirements)
    // ------------------------------------------------------------------

    private static string NewConfigExpression(string acceptanceRoot) =>
        "$script:TestConfig = [pscustomobject]@{schema_version=1; acceptance_root='" + acceptanceRoot.Replace("'", "''") + "'; " +
        "run=[pscustomobject]@{planned_hours=168; started_at_local=(Get-Date).AddHours(-3).ToString('o')}; " +
        "rc=[pscustomobject]@{release_tag='v0.6.0-rc.1'; asset_url='https://example.invalid/WeArchive-win-x64.zip'; " +
        "asset_sha256='" + new string('a', 64) + "'; expected_version='0.6.0-rc.1'; commit_sha='" + new string('b', 40) + "'}; " +
        "capture=[pscustomobject]@{account_selector='a_test000000000000'}}";

    private static string ShaPlaceholder() => new('a', 64);

    [PwshFact]
    public void BuiltCaptureRecordPassesSchemaValidation()
    {
        var tempRoot = NewTempDirectory();
        try
        {
            var result = EvalObject(
                NewConfigExpression(tempRoot) + "\n" +
                "$record = New-AcceptanceCaptureRecord -Config $script:TestConfig -AcceptanceRoot '" + tempRoot.Replace("'", "''") + "' " +
                "-PointIndex 3 -Outcome 'success' -ExecutedAt (Get-Date) " +
                "-CaptureResult ([pscustomobject]@{ExitCode=0;DurationMs=10;Json=[pscustomobject]@{completeness='complete';generation_id='gen_x'};SingleJsonDocument=$true}) " +
                "-VerifyResult ([pscustomobject]@{ExitCode=0;DurationMs=20;Json=[pscustomobject]@{succeeded=$true}})\n" +
                "$script:Result = @{ valid = (Assert-AcceptanceRecordValid -Record $record); " +
                "outcome = $record.planned_point.outcome; generation = $record.capture.generation_id; " +
                "rc_sha = $record.rc.asset_sha256; vault_root = $record.environment.vault_root }");
            Assert.True(result.GetProperty("valid").GetBoolean());
            Assert.Equal("success", result.GetProperty("outcome").GetString());
            Assert.Equal("gen_x", result.GetProperty("generation").GetString());
            Assert.Equal(new string('a', 64), result.GetProperty("rc_sha").GetString());
            Assert.StartsWith("<acceptance-root>/", result.GetProperty("vault_root").GetString(), StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    [PwshFact]
    public void FailedCaptureRecordWithoutExplanationIsRejected()
    {
        var result = EvalObject(
            "$script:Result = try { " +
            "$record = [pscustomobject]@{schema_version=1; record_type='capture_point'; " +
            "planned_point=[pscustomobject]@{index=1; outcome='failed_capture'; explanation=$null}; " +
            "rc=[pscustomobject]@{version='0.6.0-rc.1'; commit_sha='c'; asset_sha256='" + ShaPlaceholder() + "'}; " +
            "capture=[pscustomobject]@{exit_code=1}; vault_verify=[pscustomobject]@{}}; " +
            "Assert-AcceptanceRecordValid -Record $record; @{threw=$false} } catch { @{threw=$true; message=$_.Exception.Message} }");
        Assert.True(result.GetProperty("threw").GetBoolean(),
            "A failed_capture record without an explanation must be rejected (no unexplained failures).");
    }

    [PwshFact]
    public void RecordMissingRcBindingIsRejected()
    {
        var result = EvalObject(
            "$script:Result = try { " +
            "$record = [pscustomobject]@{schema_version=1; record_type='capture_point'; " +
            "planned_point=[pscustomobject]@{index=1; outcome='success'; explanation=$null}; " +
            "rc=[pscustomobject]@{version='0.6.0-rc.1'}; " +
            "capture=[pscustomobject]@{exit_code=0}; vault_verify=[pscustomobject]@{}}; " +
            "Assert-AcceptanceRecordValid -Record $record; @{threw=$false} } catch { @{threw=$true; message=$_.Exception.Message} }");
        Assert.True(result.GetProperty("threw").GetBoolean(),
            "The exact-RC binding (commit SHA, asset hash) must be present for auditability.");
    }

    [PwshFact]
    public void MissedScenarioRecordWithoutRcBindingIsRejected()
    {
        var result = EvalObject(
            "$script:Result = try { " +
            "$record = [pscustomobject]@{schema_version=1; record_type='failure_boundary'; scenario_step='x'; " +
            "executed_at=(Get-Date).ToString('o')}; " +
            "Assert-AcceptanceRecordValid -Record $record; @{threw=$false} } catch { @{threw=$true; message=$_.Exception.Message} }");
        Assert.True(result.GetProperty("threw").GetBoolean(),
            "Scenario records must still bind the exact RC.");
    }

    // ------------------------------------------------------------------
    // Trace writes (idempotency: re-runs never duplicate a point)
    // ------------------------------------------------------------------

    [PwshFact]
    public void RewritingTheSamePointReplacesInsteadOfDuplicating()
    {
        var tempTrace = Path.Combine(
            Path.GetTempPath(), "wearchive-acceptance-tests", Guid.NewGuid().ToString("n") + ".jsonl");
        try
        {
            var result = EvalObject(
                "$trace = '" + tempTrace.Replace("'", "''") + "'\n" +
                "$record = [pscustomobject]@{schema_version=1; record_type='capture_point'; " +
                "planned_point=[pscustomobject]@{index=7; outcome='failed_capture'; explanation='initial attempt failed'}; " +
                "rc=[pscustomobject]@{version='0.6.0-rc.1'; commit_sha='c'; asset_sha256='" + ShaPlaceholder() + "'}; " +
                "capture=[pscustomobject]@{exit_code=1}; vault_verify=[pscustomobject]@{}}\n" +
                "$first = Write-AcceptanceTraceRecord -TracePath $trace -Record $record\n" +
                "$retry = [pscustomobject]@{schema_version=1; record_type='capture_point'; " +
                "planned_point=[pscustomobject]@{index=7; outcome='success'; explanation=$null}; " +
                "rc=[pscustomobject]@{version='0.6.0-rc.1'; commit_sha='c'; asset_sha256='" + ShaPlaceholder() + "'}; " +
                "capture=[pscustomobject]@{exit_code=0}; vault_verify=[pscustomobject]@{}}\n" +
                "$second = Write-AcceptanceTraceRecord -TracePath $trace -Record $retry\n" +
                "$read = @(Read-AcceptanceTrace -TracePath $trace)\n" +
                "$script:Result = @{ first = $first; second = $second; count = $read.Count; outcome = $read[0].planned_point.outcome }");
            Assert.Equal("appended", result.GetProperty("first").GetString());
            Assert.Equal("replaced", result.GetProperty("second").GetString());
            Assert.Equal(1, result.GetProperty("count").GetInt32());
            Assert.Equal("success", result.GetProperty("outcome").GetString());
        }
        finally
        {
            TryDeleteFile(tempTrace);
        }
    }

    [PwshFact]
    public void ScenarioRecordsReplaceByStepIdentity()
    {
        var tempTrace = Path.Combine(
            Path.GetTempPath(), "wearchive-acceptance-tests", Guid.NewGuid().ToString("n") + ".jsonl");
        try
        {
            var result = EvalObject(
                "$trace = '" + tempTrace.Replace("'", "''") + "'\n" +
                "$boundary = {param($passed) [pscustomobject]@{schema_version=1; record_type='failure_boundary'; " +
                "scenario_step='boundary-derived-index'; rc_version='0.6.0-rc.1'; rc_commit_sha='c'; " +
                "rc_asset_sha256='" + ShaPlaceholder() + "'; executed_at=(Get-Date).ToString('o'); passed=$passed; observation='o'}}\n" +
                "$null = Write-AcceptanceTraceRecord -TracePath $trace -Record (& $boundary $false)\n" +
                "$null = Write-AcceptanceTraceRecord -TracePath $trace -Record (& $boundary $true)\n" +
                "$read = @(Read-AcceptanceTrace -TracePath $trace)\n" +
                "$script:Result = @{ count = $read.Count; passed = $read[0].passed }");
            Assert.Equal(1, result.GetProperty("count").GetInt32());
            Assert.True(result.GetProperty("passed").GetBoolean());
        }
        finally
        {
            TryDeleteFile(tempTrace);
        }
    }

    // ------------------------------------------------------------------
    // Missed-point accounting (no planned point silently skipped)
    // ------------------------------------------------------------------

    [PwshFact]
    public void UnrecordedPastPointsAreClassifiedAsMissed()
    {
        // Deterministic against hour boundaries: the expectation is derived from the
        // same config inside the same snippet.
        var result = EvalObject(
            "$config = [pscustomobject]@{schema_version=1; acceptance_root='C:\\does-not-exist'; " +
            "run=[pscustomobject]@{planned_hours=168; started_at_local=(Get-Date).AddHours(-90).AddMinutes(-30).ToString('o')}; " +
            "rc=[pscustomobject]@{release_tag='v0.6.0-rc.1'; asset_url='u'; asset_sha256='" + ShaPlaceholder() + "'; " +
            "expected_version='0.6.0-rc.1'; commit_sha='c'}; " +
            "capture=[pscustomobject]@{account_selector='a_test000000000000'}}\n" +
            "$now = Get-Date\n" +
            "$current = Get-AcceptanceCurrentPointIndex -Config $config -At $now\n" +
            "$missed = @(Get-AcceptanceMissedPointIndexes -Config $config -RecordedIndexes @(0, 1, 2) -At $now)\n" +
            "$script:Result = @{ missed = $missed; current = $current }");

        var current = result.GetProperty("current").GetInt32();
        var missed = result.GetProperty("missed");
        var expectedMissed = Enumerable.Range(0, current).Where(index => index is not (0 or 1 or 2)).ToArray();
        Assert.Equal(expectedMissed.Length, missed.GetArrayLength());
        for (var i = 0; i < expectedMissed.Length; i++)
        {
            Assert.Equal(expectedMissed[i], missed[i].GetInt32());
        }
        // The run must be far enough along for the test to be meaningful.
        Assert.True(current >= 3, "expected the 90-minute-old run to have at least 3 past points");
    }

    // ------------------------------------------------------------------
    // Privacy-safe path rendering
    // ------------------------------------------------------------------

    [PwshFact]
    public void PathsInsideTheAcceptanceRootRenderRelative()
    {
        var tempRoot = NewTempDirectory();
        try
        {
            var inside = Path.Combine(tempRoot, "home", "rawvault");
            var result = EvalObject(
                "$script:Result = @{ inside = (ConvertTo-PrivacySafePath -Path '" + inside.Replace("'", "''") +
                "' -AcceptanceRoot '" + tempRoot.Replace("'", "''") + "'); " +
                "outside = (ConvertTo-PrivacySafePath -Path 'C:\\some\\other\\place' -AcceptanceRoot '" +
                tempRoot.Replace("'", "''") + "'); " +
                "empty = (ConvertTo-PrivacySafePath -Path $null -AcceptanceRoot '" + tempRoot.Replace("'", "''") + "') }");
            Assert.Equal("<acceptance-root>/home/rawvault", result.GetProperty("inside").GetString());
            Assert.Equal("<external>", result.GetProperty("outside").GetString());
            Assert.Equal(JsonValueKind.Null, result.GetProperty("empty").ValueKind);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    // ------------------------------------------------------------------
    // Config validation
    // ------------------------------------------------------------------

    [PwshFact]
    public void ConfigMissingTheRcBindingIsRejected()
    {
        var tempDir = NewTempDirectory();
        try
        {
            var brokenConfig = Path.Combine(tempDir, "config.json");
            File.WriteAllText(brokenConfig, """
                {
                  "schema_version": 1,
                  "acceptance_root": "C:\\tmp",
                  "run": { "planned_hours": 168, "started_at_local": "2026-10-06T09:00:00+08:00" },
                  "rc": { "release_tag": "v0.6.0-rc.1" },
                  "capture": { "account_selector": "a_test000000000000" }
                }
                """);
            var result = EvalObject(
                "$script:Result = try { Read-AcceptanceConfig -Path '" + brokenConfig.Replace("'", "''") + "'; @{threw=$false} } " +
                "catch { @{threw=$true; message=$_.Exception.Message} }");
            Assert.True(result.GetProperty("threw").GetBoolean());
            Assert.Contains("rc.asset_url", result.GetProperty("message").GetString(), StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(tempDir);
        }
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static string NewTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "wearchive-acceptance-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }
}
