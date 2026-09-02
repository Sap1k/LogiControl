// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;
using System.Text;
using LogiControl.Protocol;

namespace LogiControl.Broker.Tests;

public sealed class BrokerRequestDispatcherTests
{
    [Fact]
    public void HelloBindUpsertAndStartAreSemanticallyAcknowledged()
    {
        using var fixture = new RuntimeFixture(deviceReady: true);
        BrokerRequestDispatcher dispatcher = fixture.Dispatcher;

        IpcFrame hello = dispatcher.Dispatch(Request(IpcMessageType.Hello, 1, 0, []));
        Assert.Equal(BrokerResult.Ok, Result(hello));
        Assert.NotEqual((ulong)0, hello.Header.SessionId);

        byte[] binding = { 4, 0, (byte)'D', (byte)'F', (byte)'G', (byte)'T' };
        Assert.Equal(BrokerResult.Ok, Result(dispatcher.Dispatch(
            Request(IpcMessageType.BindDevice, 2, hello.Header.SessionId, binding))));

        var effect = new ConstantEffectDefinition(
            new EffectCommon(EffectCommon.InfiniteDuration, 0, 0, 10_000, 10_000, null), 2_500);
        byte[] encoded = new byte[EffectDefinitionCodec.GetEncodedLength(effect)];
        EffectDefinitionCodec.TryWrite(encoded, effect, out _);
        var upsertPayload = new byte[8 + encoded.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(upsertPayload.AsSpan(4), (ushort)EffectUpdateMask.All);
        encoded.CopyTo(upsertPayload, 8);
        IpcFrame upsert = dispatcher.Dispatch(Request(IpcMessageType.UpsertEffect, 3, hello.Header.SessionId, upsertPayload));
        Assert.Equal(BrokerResult.Ok, Result(upsert));
        uint handle = BinaryPrimitives.ReadUInt32LittleEndian(upsert.Payload.AsSpan(4));
        Assert.NotEqual(0u, handle);

        var start = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(start, handle);
        BinaryPrimitives.WriteUInt32LittleEndian(start.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(start.AsSpan(8), 2);
        Assert.Equal(BrokerResult.Ok, Result(dispatcher.Dispatch(
            Request(IpcMessageType.StartEffect, 4, hello.Header.SessionId, start))));
    }

    [Fact]
    public void StartIsRejectedUntilDeviceBindingIsReady()
    {
        using var fixture = new RuntimeFixture(deviceReady: false);
        BrokerRequestDispatcher dispatcher = fixture.Dispatcher;
        IpcFrame hello = dispatcher.Dispatch(Request(IpcMessageType.Hello, 1, 0, []));
        byte[] binding = { 1, 0, (byte)'x' };
        Assert.Equal(BrokerResult.DeviceNotReady, Result(dispatcher.Dispatch(
            Request(IpcMessageType.BindDevice, 2, hello.Header.SessionId, binding))));

        var start = new byte[12];
        Assert.Equal(BrokerResult.DeviceNotReady, Result(dispatcher.Dispatch(
            Request(IpcMessageType.StartEffect, 3, hello.Header.SessionId, start))));
    }

    [Fact]
    public void EffectMutationIsRejectedBeforeSuccessfulBinding()
    {
        using var fixture = new RuntimeFixture(deviceReady: true);
        IpcFrame hello = fixture.Dispatcher.Dispatch(Request(IpcMessageType.Hello, 1, 0, []));
        var effect = new ConstantEffectDefinition(
            new EffectCommon(1_000, 0, 0, 10_000, 10_000, null), 1_000);
        byte[] encoded = new byte[EffectDefinitionCodec.GetEncodedLength(effect)];
        Assert.True(EffectDefinitionCodec.TryWrite(encoded, effect, out _));
        var payload = new byte[8 + encoded.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(4), (ushort)EffectUpdateMask.All);
        encoded.CopyTo(payload, 8);

        Assert.Equal(BrokerResult.DeviceNotReady, Result(fixture.Dispatcher.Dispatch(
            Request(IpcMessageType.UpsertEffect, 2, hello.Header.SessionId, payload))));
    }

    [Fact]
    public void StartIsRejectedIfDeviceIsRemovedAfterSuccessfulBinding()
    {
        bool ready = true;
        using var fixture = new RuntimeFixture(() => ready);
        BrokerRequestDispatcher dispatcher = fixture.Dispatcher;
        IpcFrame hello = dispatcher.Dispatch(Request(IpcMessageType.Hello, 1, 0, []));
        byte[] binding = { 1, 0, (byte)'x' };
        Assert.Equal(BrokerResult.Ok, Result(dispatcher.Dispatch(
            Request(IpcMessageType.BindDevice, 2, hello.Header.SessionId, binding))));

        ready = false;
        var start = new byte[12];
        Assert.Equal(BrokerResult.DeviceNotReady, Result(dispatcher.Dispatch(
            Request(IpcMessageType.StartEffect, 3, hello.Header.SessionId, start))));
    }

    [Fact]
    public void MalformedVersionFlagsSessionAndPayloadAreRejected()
    {
        using var fixture = new RuntimeFixture(deviceReady: true);
        BrokerRequestDispatcher dispatcher = fixture.Dispatcher;
        IpcFrameHeader badVersion = new(2, 0, IpcMessageType.Hello, IpcFrameFlags.None, 0, 1, 0);
        Assert.Equal(BrokerResult.ProtocolError, Result(dispatcher.Dispatch(new IpcFrame(badVersion, []))));

        IpcFrame hello = dispatcher.Dispatch(Request(IpcMessageType.Hello, 2, 0, []));
        Assert.Equal(BrokerResult.InputLost, Result(dispatcher.Dispatch(
            Request(IpcMessageType.Heartbeat, 3, hello.Header.SessionId + 1, []))));
        Assert.Equal(BrokerResult.InvalidArgument, Result(dispatcher.Dispatch(
            Request(IpcMessageType.Heartbeat, 4, hello.Header.SessionId, [1]))));
    }

    [Fact]
    public void Version10ClientRemainsAcceptedAndReceivesVersion10Response()
    {
        using var fixture = new RuntimeFixture(deviceReady: true);

        IpcFrame response = fixture.Dispatcher.Dispatch(Request(IpcMessageType.Hello, 1, 0, [], minorVersion: 0));

        Assert.Equal(BrokerResult.Ok, Result(response));
        Assert.Equal((ushort)0, response.Header.MinorVersion);
    }

    [Fact]
    public void BindingRequiresExactSelectedReadyPathAndIsInvalidatedBySelectionChange()
    {
        string selectedPath = "path-selected";
        using var fixture = new RuntimeFixture(path => string.Equals(path, selectedPath, StringComparison.Ordinal));
        IpcFrame hello = fixture.Dispatcher.Dispatch(Request(IpcMessageType.Hello, 1, 0, []));

        Assert.Equal(BrokerResult.DeviceNotReady, Result(fixture.Dispatcher.Dispatch(
            Request(IpcMessageType.BindDevice, 2, hello.Header.SessionId, BindPayload("path-invented")))));
        Assert.Equal(BrokerResult.Ok, Result(fixture.Dispatcher.Dispatch(
            Request(IpcMessageType.BindDevice, 3, hello.Header.SessionId, BindPayload(selectedPath)))));

        selectedPath = "path-other-wheel";
        Assert.Equal(BrokerResult.DeviceNotReady, Result(fixture.Dispatcher.Dispatch(
            Request(IpcMessageType.StartEffect, 4, hello.Header.SessionId, new byte[12]))));
    }

    [Fact]
    public void Version11ListsCandidatesAndSelectsBoundedDeviceId()
    {
        ulong selected = ulong.MaxValue;
        WheelCandidateInfo[] candidates =
        [
            new(7, WheelModel.G27, "G27 Racing Wheel", 0x1234, 0xC29B, "path-g27", 7, true, true),
        ];
        using var fixture = new RuntimeFixture(
            static _ => true,
            () => candidates,
            id =>
            {
                selected = id;
                return id is 0 or 7;
            });
        IpcFrame hello = fixture.Dispatcher.Dispatch(Request(
            IpcMessageType.Hello, 1, 0, [], minorVersion: 1));

        IpcFrame listed = fixture.Dispatcher.Dispatch(Request(
            IpcMessageType.QueryWheelCandidates, 2, hello.Header.SessionId, [], minorVersion: 1));
        Assert.Equal(BrokerResult.Ok, Result(listed));
        Assert.True(WheelCandidateCodec.TryDecode(listed.Payload.AsSpan(4), out IReadOnlyList<WheelCandidateInfo>? decoded));
        Assert.Equal(candidates, decoded);

        var selection = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(selection, 7);
        Assert.Equal(BrokerResult.Ok, Result(fixture.Dispatcher.Dispatch(Request(
            IpcMessageType.SelectWheel, 3, hello.Header.SessionId, selection, minorVersion: 1))));
        Assert.Equal((ulong)7, selected);
        Assert.Equal(BrokerResult.InvalidArgument, Result(fixture.Dispatcher.Dispatch(Request(
            IpcMessageType.SelectWheel, 4, hello.Header.SessionId, [1], minorVersion: 1))));
    }

    [Fact]
    public void StatusAndEmergencyStopAreGlobalControlOperations()
    {
        using var fixture = new RuntimeFixture(deviceReady: true);
        ulong owner = fixture.Invoke(() =>
        {
            Assert.Equal(BrokerResult.Ok, fixture.Coordinator.OpenSession(out ulong session));
            Assert.Equal(BrokerResult.Ok, fixture.Coordinator.UpsertEffect(session, 0, EffectUpdateMask.All,
                new ConstantEffectDefinition(
                    new EffectCommon(EffectCommon.InfiniteDuration, 0, 0, 10_000, 10_000, null), 1_000),
                false, out uint handle));
            Assert.Equal(BrokerResult.Ok, fixture.Coordinator.StartEffect(session, handle, 1, false, true));
            return session;
        });
        IpcFrame hello = fixture.Dispatcher.Dispatch(Request(IpcMessageType.Hello, 1, 0, []));

        IpcFrame status = fixture.Dispatcher.Dispatch(
            Request(IpcMessageType.QueryStatus, 2, hello.Header.SessionId, []));
        Assert.Equal(BrokerResult.Ok, Result(status));
        Assert.Equal(owner, BinaryPrimitives.ReadUInt64LittleEndian(status.Payload.AsSpan(4)));
        Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(status.Payload.AsSpan(12)));
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(status.Payload.AsSpan(16)));
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(status.Payload.AsSpan(20)));
        Assert.All(status.Payload.AsSpan(24, 4).ToArray(), static value => Assert.Equal((byte)0, value));

        Assert.Equal(BrokerResult.Ok, Result(fixture.Dispatcher.Dispatch(
            Request(IpcMessageType.EmergencyStop, 3, hello.Header.SessionId, []))));
        Assert.Equal((ulong)0, fixture.Invoke(() => fixture.Coordinator.OwnerSessionId));
    }

    [Fact]
    public void TelemetryWithoutOutputSinkReturnsZeroedOutputCounters()
    {
        using var fixture = new RuntimeFixture(deviceReady: true);
        IpcFrame hello = fixture.Dispatcher.Dispatch(Request(IpcMessageType.Hello, 1, 0, []));

        for (ulong requestId = 2; requestId < 12; requestId++)
        {
            IpcFrame telemetry = fixture.Dispatcher.Dispatch(
                Request(IpcMessageType.QueryTelemetry, requestId, hello.Header.SessionId, []));
            Assert.Equal(BrokerResult.Ok, Result(telemetry));
            Assert.Equal(108, telemetry.Payload.Length);
            Assert.All(telemetry.Payload.AsSpan(52, 56).ToArray(),
                static value => Assert.Equal((byte)0, value));
        }
    }

    [Fact]
    public async Task FrameStreamRejectsTruncatedDeclaredPayload()
    {
        var bytes = new byte[IpcFrameCodec.HeaderLength + 1];
        var header = new IpcFrameHeader(1, 0, IpcMessageType.Heartbeat, IpcFrameFlags.None, 2, 1, 1);
        Assert.True(IpcFrameCodec.TryWriteHeader(bytes, header));
        await using var stream = new MemoryStream(bytes, writable: false);
        await Assert.ThrowsAsync<EndOfStreamException>(async () =>
            await IpcFrameStream.ReadAsync(stream, TestContext.Current.CancellationToken));
    }

    private static IpcFrame Request(
        IpcMessageType type,
        ulong requestId,
        ulong sessionId,
        byte[] payload,
        ushort minorVersion = 0) =>
        new(new IpcFrameHeader(1, minorVersion, type, IpcFrameFlags.None,
            (uint)payload.Length, requestId, sessionId), payload);

    private static byte[] BindPayload(string path)
    {
        byte[] pathBytes = Encoding.UTF8.GetBytes(path);
        var payload = new byte[2 + pathBytes.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, (ushort)pathBytes.Length);
        pathBytes.CopyTo(payload, 2);
        return payload;
    }

    private static BrokerResult Result(IpcFrame response) =>
        (BrokerResult)BinaryPrimitives.ReadInt32LittleEndian(response.Payload);

    private sealed class RuntimeFixture : IDisposable
    {
        private readonly EffectRuntime runtime;

        public RuntimeFixture(bool deviceReady)
            : this(() => deviceReady)
        {
        }

        public RuntimeFixture(Func<bool> deviceReady)
        {
            var clock = new QpcMonotonicClock();
            Coordinator = new BrokerSessionCoordinator(clock);
            runtime = new EffectRuntime(Coordinator, clock, new NullForceFeedbackOutputSink());
            runtime.Start();
            Dispatcher = new BrokerRequestDispatcher(
                Coordinator, runtime, _ => deviceReady(), deviceReady, static () => [], static _ => false);
        }

        public RuntimeFixture(Func<string, bool> bindDevice)
            : this(bindDevice, static () => [], static _ => false)
        {
        }

        public RuntimeFixture(
            Func<string, bool> bindDevice,
            Func<IReadOnlyList<WheelCandidateInfo>> candidates,
            Func<ulong, bool> select)
        {
            var clock = new QpcMonotonicClock();
            Coordinator = new BrokerSessionCoordinator(clock);
            runtime = new EffectRuntime(Coordinator, clock, new NullForceFeedbackOutputSink());
            runtime.Start();
            Dispatcher = new BrokerRequestDispatcher(Coordinator, runtime, bindDevice, candidates, select);
        }

        public BrokerRequestDispatcher Dispatcher { get; }

        public BrokerSessionCoordinator Coordinator { get; }

        public T Invoke<T>(Func<T> command) => runtime.Invoke(command, TimeSpan.FromSeconds(1));

        public void Dispose()
        {
            Dispatcher.CloseAfterTransportLoss();
            runtime.Dispose();
        }
    }
}
