#nullable enable
//-------------------
// Reachable Games
// Copyright 2026
//-------------------
// The typed message layer: applications deal in strongly typed message objects; all the pooled-buffer accounting stays
// inside the library where it cannot be gotten wrong.
//
// IRGMessage is deliberately tiny: a message only knows its integer type id, so dispatch is a switch or array lookup,
// never reflection (IL2CPP/AOT friendly).  IMessageFactory owns BOTH serialize and deserialize, because the codec is a
// strategy: the same message classes can ride a sloppy JSON codec during development and a packed binary codec in
// production, swapped by passing a different factory -- which is impossible if serialization is buried in each message.
//
// Wire format (binary frames only): [int32 little-endian type id][payload bytes].
//
// SAFETY: Deserialize receives a ReadOnlySpan<byte>.  Spans are stack-only, so the COMPILER enforces that no reference
// to the pooled buffer can outlive the call -- escape is structurally impossible, not just discouraged by comments.

using System;
using System.Buffers;
using System.Buffers.Binary;

namespace ReachableGames
{
	namespace RGWebSocket
	{
		public interface IRGMessage
		{
			int TypeId { get; }
		}

		//-------------------
		// Serializes [typeId][payload] into a pooled buffer via a per-thread writer, so both RGConnectionManager and
		// RGUnityWebSocket speak the identical wire format from one place.  The caller owns the returned reference.
		static public class RGMessagePacker
		{
			[ThreadStatic] static private PooledBufferWriter? tWriter;  // serialize is synchronous within a Send call, so per-thread reuse is safe and allocation-free

			static public PooledArray Pack(IMessageFactory factory, IRGMessage msg)
			{
				PooledBufferWriter writer = tWriter ??= new PooledBufferWriter();
				writer.Begin(256);
				BinaryPrimitives.WriteInt32LittleEndian(writer.GetSpan(4), msg.TypeId);
				writer.Advance(4);
				factory.Serialize(msg, writer);
				return writer.Detach();
			}
		}

		public interface IMessageFactory
		{
			// Write msg's payload (NOT the type id header -- the library writes that) into the writer.
			void Serialize(IRGMessage msg, IBufferWriter<byte> writer);

			// Parse a payload into a message instance.  Return null for unknown type ids or malformed payloads; the library
			// treats that as a protocol violation.  Runs on the socket's receive task, so parsing parallelizes across connections.
			IRGMessage? Deserialize(int typeId, ReadOnlySpan<byte> payload);
		}

		//-------------------
		// IBufferWriter<byte> over pooled buffers: borrows from PooledArray, grows by doubling out of the pool (one copy per
		// double, same as the receive path), and Detach() hands the finished buffer to the send queue.  Serializers never see
		// a PooledArray, and standard serializer libraries (System.Text.Json, MessagePack, protobuf) all write to IBufferWriter.
		// NOT thread-safe -- the typed layer keeps one per thread via [ThreadStatic].
		public class PooledBufferWriter : IBufferWriter<byte>
		{
			private PooledArray? _buffer = null;
			private int          _written = 0;

			public void Begin(int initialCapacity)
			{
				if (_buffer!=null)  // a previous serialize threw between Begin and Detach; don't leak its buffer
					using (_buffer) { }
				_buffer = PooledArray.BorrowFromPool(initialCapacity);
				_written = 0;
			}

			public void Advance(int count)
			{
				_written += count;
			}

			public Memory<byte> GetMemory(int sizeHint = 0)
			{
				EnsureCapacity(sizeHint<=0 ? 256 : sizeHint);
				return new Memory<byte>(_buffer!.data, _written, _buffer.data.Length - _written);
			}

			public Span<byte> GetSpan(int sizeHint = 0)
			{
				EnsureCapacity(sizeHint<=0 ? 256 : sizeHint);
				return new Span<byte>(_buffer!.data, _written, _buffer.data.Length - _written);
			}

			// The finished message: Length is set to exactly what was written, and the caller owns the reference.
			public PooledArray Detach()
			{
				PooledArray ret = _buffer!;
				ret.Length = _written;
				_buffer = null;
				_written = 0;
				return ret;
			}

			private void EnsureCapacity(int size)
			{
				if (_written + size > _buffer!.data.Length)
				{
					PooledArray bigger = PooledArray.BorrowFromPool(Math.Max(_buffer.data.Length * 2, _written + size));
					Buffer.BlockCopy(_buffer.data, 0, bigger.data, 0, _written);
					using (_buffer) { }  // old bucket goes back to the pool
					_buffer = bigger;
				}
			}
		}
	}
}
