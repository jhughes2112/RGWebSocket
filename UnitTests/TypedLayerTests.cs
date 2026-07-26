//-------------------
// Reachable Games
// Copyright 2026
//-------------------
// The typed-message plumbing: PooledBufferWriter (an IBufferWriter handed to FOREIGN serializers, so its
// validation is a security boundary) and RGMessagePacker (the one place the wire format is written).
// NOTE: the packer's writer is [ThreadStatic], so packer tests are deliberately await-free -- everything in
// one body runs on one thread, same as a real Send call.

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Threading.Tasks;

namespace ReachableGames
{
	namespace RGWebSocket
	{
		namespace UnitTests
		{
			// Minimal typed vocabulary for these tests.
			public class BlobMsg : IRGMessage
			{
				public const int    kTypeId = 7;
				public       int    TypeId => kTypeId;
				public       byte[] Payload = Array.Empty<byte>();
			}

			public class BlobFactory : IMessageFactory
			{
				public void Serialize(IRGMessage msg, IBufferWriter<byte> writer)
				{
					writer.Write(((BlobMsg)msg).Payload);
				}

				public IRGMessage Deserialize(int typeId, ReadOnlySpan<byte> payload)
				{
					return typeId == BlobMsg.kTypeId ? new BlobMsg() { Payload = payload.ToArray() } : null;
				}
			}

			public class ThrowingFactory : IMessageFactory
			{
				public void       Serialize  (IRGMessage msg, IBufferWriter<byte> writer) { throw new InvalidOperationException("deliberate serializer fault"); }
				public IRGMessage Deserialize(    int typeId, ReadOnlySpan<byte> payload) { return null; }
			}

			static public class TypedLayerTests
			{
				static public Task Run()
				{
					return Runner.Group("PooledBufferWriter / RGMessagePacker",
						("write, detach, exact length", () =>
						{
							long               baseline = PooledArray.GetLiveAllocs();
							PooledBufferWriter writer   = new PooledBufferWriter();
							writer.Begin(256);
							Span<byte> span = writer.GetSpan(4);
							BinaryPrimitives.WriteInt32LittleEndian(span, 0x11223344);
							writer.Advance(4);
							PooledArray done = writer.Detach();
							Expect.Eq(4, done.Length, "Length is exactly what was written");
							Expect.Eq(0x11223344, BinaryPrimitives.ReadInt32LittleEndian(new ReadOnlySpan<byte>(done.data, 0, 4)), "content intact");
							((IDisposable)done).Dispose();
							Expect.Eq(baseline, PooledArray.GetLiveAllocs(), "no buffer left behind");
							return Task.CompletedTask;
						}
					),
						("growth across buckets preserves every byte", () =>
						{
							PooledBufferWriter writer = new PooledBufferWriter();
							writer.Begin(256);
							const int kBytes = 1000; // 256 -> 512 -> 1024: two doublings
							for (int i = 0; i < kBytes; i++)
							{
								writer.GetSpan(1)[0] = (byte)(i * 7);
								writer.Advance(1);
							}
							PooledArray done = writer.Detach();
							Expect.Eq(kBytes, done.Length, "final length");
							for (int i = 0; i < kBytes; i++)
								if (done.data[i] != (byte)(i * 7))
									throw new Expect.TestFailure($"byte {i} lost during growth: got {done.data[i]}");
							((IDisposable)done).Dispose();
							return Task.CompletedTask;
						}
					),
						("GetMemory path works too", () =>
						{
							PooledBufferWriter writer = new PooledBufferWriter();
							writer.Begin(16);
							Memory<byte> mem = writer.GetMemory(3);
							mem.Span[0]      = 1;
							mem.Span[1]      = 2;
							mem.Span[2]      = 3;
							writer.Advance(3);
							PooledArray done = writer.Detach();
							Expect.Eq(      3,  done.Length, "length via GetMemory");
							Expect.Eq((byte)2, done.data[1], "content via GetMemory");
							((IDisposable)done).Dispose();
							return Task.CompletedTask;
						}
					),
						("Advance is validated against hostile serializers", () =>
						{
							PooledBufferWriter fresh = new PooledBufferWriter();
							Expect.Throws<InvalidOperationException>(() => fresh.Advance(1), "Advance before Begin");
							PooledBufferWriter writer = new PooledBufferWriter();
							writer.Begin(256);
							Expect.Throws<ArgumentOutOfRangeException>(() => writer.Advance(-1), "negative Advance");
							Expect.Throws<ArgumentOutOfRangeException>(() => writer.Advance(4097), "Advance past capacity would ship the pool's stale bytes -- another connection's data");
							writer.Advance(10); // still usable after refused Advances
							PooledArray done = writer.Detach();
							Expect.Eq(10, done.Length, "writer intact after refused Advances");
							((IDisposable)done).Dispose();
							return Task.CompletedTask;
						}
					),
						("Begin after an abandoned serialize releases the stranded buffer", () =>
						{
							long               baseline = PooledArray.GetLiveAllocs();
							PooledBufferWriter writer   = new PooledBufferWriter();
							writer.Begin(256); // abandoned: no Detach, as if a serializer threw
							writer.Begin(256); // must release the stranded one, not leak it
							PooledArray done = writer.Detach();
							((IDisposable)done).Dispose();
							Expect.Eq(baseline, PooledArray.GetLiveAllocs(), "abandoned buffer reclaimed by the next Begin");
							return Task.CompletedTask;
						}
					),
						("Pack writes [typeId LE][payload]", () =>
						{
							BlobMsg msg = new BlobMsg() { Payload = new byte[] { 0xAA, 0xBB, 0xCC } };
							using (PooledArray packed = RGMessagePacker.Pack(new BlobFactory(), msg))
							{
								Expect.Eq(7, packed.Length, "4 header + 3 payload");
								Expect.Eq(BlobMsg.kTypeId, BinaryPrimitives.ReadInt32LittleEndian(new ReadOnlySpan<byte>(packed.data, 0, 4)), "type id header, little-endian");
								Expect.Eq((byte)0xAA, packed.data[4], "payload byte 0");
								Expect.Eq((byte)0xCC, packed.data[6], "payload byte 2");
							}
							return Task.CompletedTask;
						}
					),
						("a throwing serializer doesn't wedge the thread's packer", () =>
						{
							long baseline = PooledArray.GetLiveAllocs();
							Expect.Throws<InvalidOperationException>(() => RGMessagePacker.Pack(new ThrowingFactory(), new BlobMsg()), "serializer faults propagate loudly");
							// The buffer from the failed Pack is retained by the [ThreadStatic] writer until the next Pack reclaims it.
							BlobMsg msg = new BlobMsg() { Payload = new byte[] { 1, 2 } };
							using (PooledArray packed = RGMessagePacker.Pack(new BlobFactory(), msg))
								Expect.Eq(6, packed.Length, "packer fully functional after the fault");
							Expect.Eq(baseline, PooledArray.GetLiveAllocs(), "the stranded buffer was reclaimed, nothing leaked");
							return Task.CompletedTask;
						}
					),
						("pack/deserialize roundtrip", () =>
						{
							byte[] payload = new byte[500];
							new Random(42).NextBytes(payload);
							BlobFactory factory = new BlobFactory();
							using (PooledArray packed = RGMessagePacker.Pack(factory, new BlobMsg() { Payload = payload }))
							{
								int        typeId  = BinaryPrimitives.ReadInt32LittleEndian(new ReadOnlySpan<byte>(packed.data, 0, 4));
								IRGMessage decoded = factory.Deserialize(typeId, new ReadOnlySpan<byte>(packed.data, 4, packed.Length - 4));
								Expect.True(decoded is BlobMsg, "type id dispatched");
								byte[] got = ((BlobMsg)decoded).Payload;
								Expect.Eq(payload.Length, got.Length, "payload length survived");
								for (int i = 0; i < payload.Length; i++)
									if (payload[i] != got[i])
										throw new Expect.TestFailure($"payload byte {i} corrupted");
								Expect.Eq(null, factory.Deserialize(999, new ReadOnlySpan<byte>(packed.data, 4, packed.Length - 4)), "unknown type id rejected");
							}
							return Task.CompletedTask;
						}
					));
				}
			}
		}
	}
}