// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;

namespace LogiControl.Broker;

public static class RuntimeProfileCommand
{
    public static async Task<int> RunAsync(string[] arguments)
    {
        int seconds = ReadInt(arguments, "--seconds", 60, 1, 3_600);
        int runs = ReadInt(arguments, "--runs", 10, 1, 100);
        bool stress = arguments.Contains("--stress", StringComparer.OrdinalIgnoreCase);
        bool events = arguments.Contains("--profile-events", StringComparer.OrdinalIgnoreCase);
        string[] known = ["--seconds", "--runs", "--stress", "--profile-events"];
        for (int i = 0; i < arguments.Length; i++)
        {
            if (!known.Contains(arguments[i], StringComparer.OrdinalIgnoreCase) &&
                (i == 0 || !known.Take(2).Contains(arguments[i - 1], StringComparer.OrdinalIgnoreCase)))
            {
                throw new ArgumentException($"Unknown profile-runtime option '{arguments[i]}'.");
            }
        }

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += handler;
        try
        {
            var results = new List<RuntimeProfileResult>(runs);
            for (int run = 1; run <= runs; run++)
            {
                RuntimeProfileResult result = await RuntimeProfileRunner.RunAsync(
                    TimeSpan.FromSeconds(seconds), stress, events, cancellation.Token).ConfigureAwait(false);
                results.Add(result);
                Console.WriteLine(JsonSerializer.Serialize(new { Run = run, Stress = stress, Result = result }));
            }

            Console.WriteLine(JsonSerializer.Serialize(new
            {
                Summary = new
                {
                    Runs = results.Count,
                    PassingRuns = results.Count(static result => result.MeetsProvisionalGoals),
                    WorstJitterP99Microseconds = results.Max(static result => result.AbsoluteJitterP99Microseconds),
                    WorstJitterP999Microseconds = results.Max(static result => result.AbsoluteJitterP999Microseconds),
                    WorstComputationP99Microseconds = results.Max(static result => result.MixerComputationP99Microseconds),
                    WorstCommandToMixP99Microseconds = results.Max(static result => result.CommandToMixP99Microseconds),
                    MixerAllocatedBytes = results.Sum(static result => result.MixerAllocatedBytes),
                },
            }));
            return 0;
        }
        finally
        {
            Console.CancelKeyPress -= handler;
        }
    }

    private static int ReadInt(string[] arguments, string name, int defaultValue, int minimum, int maximum)
    {
        int index = Array.FindIndex(arguments, argument => argument.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return defaultValue;
        }

        if (index + 1 >= arguments.Length || !int.TryParse(arguments[index + 1], out int value) ||
            value < minimum || value > maximum)
        {
            throw new ArgumentException($"{name} must be an integer from {minimum} through {maximum}.");
        }

        return value;
    }
}
