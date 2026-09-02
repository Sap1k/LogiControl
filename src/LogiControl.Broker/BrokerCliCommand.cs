// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using LogiControl.Hid;
using LogiControl.Protocol;

namespace LogiControl.Broker;

public static class BrokerCliCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static async Task<int> RunAsync(string[] arguments, CancellationToken cancellationToken = default)
    {
        string command = arguments[0].ToLowerInvariant();
        if (command == "list")
        {
            return await ListAsync(arguments.Contains("--json", StringComparer.OrdinalIgnoreCase), cancellationToken)
                .ConfigureAwait(false);
        }

        await using var client = new BrokerControlClient();
        await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
        switch (command)
        {
            case "status":
                Console.WriteLine(JsonSerializer.Serialize(
                    await client.QueryStatusAsync(cancellationToken).ConfigureAwait(false), JsonOptions));
                return 0;
            case "telemetry":
                Console.WriteLine(JsonSerializer.Serialize(
                    await client.QueryTelemetryAsync(cancellationToken).ConfigureAwait(false), JsonOptions));
                return 0;
            case "devices":
                Console.WriteLine(JsonSerializer.Serialize(
                    await client.QueryWheelCandidatesAsync(cancellationToken).ConfigureAwait(false), JsonOptions));
                return 0;
            case "select":
                if (arguments.Length != 2)
                {
                    throw new ArgumentException("Select requires a device ID or 'auto'.");
                }

                ulong deviceId = arguments[1].Equals("auto", StringComparison.OrdinalIgnoreCase)
                    ? 0
                    : ulong.TryParse(arguments[1], out ulong parsed) && parsed != 0
                        ? parsed
                        : throw new ArgumentException("Device ID must be a positive integer or 'auto'.");
                await client.SelectWheelAsync(deviceId, cancellationToken).ConfigureAwait(false);
                Console.WriteLine(deviceId == 0 ? "Automatic wheel selection enabled." : $"Selected wheel {deviceId}.");
                return 0;
            case "settings":
                return await SettingsAsync(client, arguments[1..], cancellationToken).ConfigureAwait(false);
            case "emergency-stop":
                await client.EmergencyStopAsync(cancellationToken).ConfigureAwait(false);
                Console.WriteLine("Emergency StopAll acknowledged by the broker runtime.");
                return 0;
            default:
                throw new ArgumentException($"Unknown broker command '{command}'.");
        }
    }

    private static async Task<int> ListAsync(bool json, CancellationToken cancellationToken)
    {
        var enumerator = new WindowsHidDeviceEnumerator();
        IReadOnlyList<HidDeviceSnapshot> devices = await enumerator.EnumerateAsync(cancellationToken).ConfigureAwait(false);
        HidDeviceSnapshot[] logitech = devices
            .Where(static device => device.VendorId == ClassicWheelCatalog.LogitechVendorId)
            .ToArray();
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(logitech, JsonOptions));
        }
        else
        {
            foreach (HidDeviceSnapshot device in logitech)
            {
                Console.WriteLine(
                    $"{device.VendorId:X4}:{device.ProductId:X4} rev {device.VersionNumber:X4} " +
                    $"usage {device.UsagePage:X4}:{device.Usage:X4} reports " +
                    $"{device.InputReportByteLength}/{device.OutputReportByteLength}/{device.FeatureReportByteLength}");
                Console.WriteLine($"  {device.InstanceId}");
                Console.WriteLine($"  path={device.DevicePath}");
            }
        }

        return logitech.Length == 0 ? 2 : 0;
    }

    private static async Task<int> SettingsAsync(
        BrokerControlClient client,
        string[] arguments,
        CancellationToken cancellationToken)
    {
        RuntimeSettings current = await client.QueryRuntimeSettingsAsync(cancellationToken).ConfigureAwait(false);
        if (arguments.Length == 0)
        {
            Console.WriteLine(JsonSerializer.Serialize(current, JsonOptions));
            return 0;
        }

        if (arguments.Length % 2 != 0)
        {
            throw new ArgumentException("Settings require option/value pairs.");
        }

        RuntimeSettings updated = current;
        for (int i = 0; i < arguments.Length; i += 2)
        {
            if (!int.TryParse(arguments[i + 1], out int value))
            {
                throw new ArgumentException($"'{arguments[i + 1]}' is not an integer.");
            }

            updated = arguments[i].ToLowerInvariant() switch
            {
                "--range" => updated with { RangeDegrees = value },
                "--master" => updated with { MasterGain = value },
                "--periodic" => updated with { PeriodicGain = value },
                "--spring" => updated with { SpringGain = value },
                "--damper" => updated with { DamperGain = value },
                "--friction" => updated with { FrictionGain = value },
                "--boundary" => updated with { BoundaryForce = value },
                "--idle-autocenter" => updated with { IdleAutocenter = value },
                _ => throw new ArgumentException($"Unknown setting '{arguments[i]}'."),
            };
        }

        if (!EffectDefinitionValidator.TryValidate(updated, out string error))
        {
            throw new ArgumentException(error);
        }

        await client.SetRuntimeSettingsAsync(updated, cancellationToken).ConfigureAwait(false);
        Console.WriteLine(JsonSerializer.Serialize(updated, JsonOptions));
        return 0;
    }
}
