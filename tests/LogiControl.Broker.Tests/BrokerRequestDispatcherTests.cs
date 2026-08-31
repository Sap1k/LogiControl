// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;
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

        Assert.Equal(BrokerResult.Ok, Result(fixture.Dispatcher.Dispatch(
            Request(IpcMessageType.EmergencyStop, 3, hello.Header.SessionId, []))));
        Assert.Equal((ulong)0, fixture.Invoke(() => fixture.Coordinator.OwnerSessionId));
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

    private static IpcFrame Request(IpcMessageType type, ulong requestId, ulong sessionId, byte[] payload) =>
        new(new IpcFrameHeader(1, 0, type, IpcFrameFlags.None, (uint)payload.Length, requestId, sessionId), payload);

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
            Dispatcher = new BrokerRequestDispatcher(Coordinator, runtime, deviceReady);
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
