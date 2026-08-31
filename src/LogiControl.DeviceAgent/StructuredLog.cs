// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;

namespace LogiControl.DeviceAgent;

internal static class StructuredLog
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    internal static void Write(string eventName, object? data = null)
    {
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            Timestamp = DateTimeOffset.UtcNow,
            Event = eventName,
            Data = data,
        }, Options));
    }

    internal static void Error(string eventName, Exception exception, object? data = null)
    {
        Console.Error.WriteLine(JsonSerializer.Serialize(new
        {
            Timestamp = DateTimeOffset.UtcNow,
            Event = eventName,
            Error = exception.Message,
            HResult = $"0x{exception.HResult:X8}",
            ExceptionType = exception.GetType().FullName,
            exception.StackTrace,
            Data = data,
        }, Options));
    }
}
