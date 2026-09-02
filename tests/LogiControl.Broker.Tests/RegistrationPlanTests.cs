// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Text.Json;

namespace LogiControl.Broker.Tests;

public sealed class RegistrationPlanTests
{
    [Fact]
    public void DryRunPlanContainsExactlyTheFourScopedPidsAndBothRegistryViews()
    {
        string repository = FindRepositoryRoot();
        var start = new ProcessStartInfo(
            "pwsh",
            $"-NoProfile -NonInteractive -File \"{Path.Combine(repository, "tools", "install", "Register-Development.ps1")}\" -PlanOnly")
        {
            WorkingDirectory = repository,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start PowerShell.");
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);

        using JsonDocument document = JsonDocument.Parse(output);
        JsonElement root = document.RootElement;
        Assert.False(root.GetProperty("changed").GetBoolean());
        Assert.Equal(
            ["C29A", "C29B", "C299", "C298"],
            root.GetProperty("productIds").EnumerateArray().Select(static item => item.GetString()).ToArray());
        Assert.Equal(8, root.GetProperty("oemRoots").GetArrayLength());
        Assert.Equal(8, root.GetProperty("forceFeedbackRoots").GetArrayLength());
        Assert.Equal(8, root.GetProperty("axisRoots").GetArrayLength());
        Assert.Equal(2, root.GetProperty("classRoots").GetArrayLength());
        Assert.Equal(
            ["conflict-check", "backup", "com", "oem-force-feedback", "axes", "manifest"],
            root.GetProperty("operationOrder").EnumerateArray().Select(static item => item.GetString()).ToArray());
        Assert.Equal("refuse-unless-explicit-replace", root.GetProperty("conflictPolicy").GetString());
        Assert.Equal("manifest-owned-only", root.GetProperty("uninstallPolicy").GetString());
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "LogiControl.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the LogiControl repository root.");
    }
}
