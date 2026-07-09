#nullable enable
//-------------------
// Reachable Games
// Copyright 2026
//-------------------
// Distribution-oriented metrics.  Totals are "number go up" and tell no story; what matters operationally is the SHAPE of
// things -- outliers versus the middle, high water marks, and categorical breakdowns (disconnects by cause).  These types
// give you that without an avalanche of numbers: each histogram is 65 longs and a handful of counters, updated lock-free.

using System;
using System.Numerics;
using System.Text;
using System.Threading;
using DataCollection;

namespace ReachableGames
{
	namespace RGWebSocket
	{
		//-------------------
		// Lock-free histogram with power-of-two buckets (matching the PooledArray sizing philosophy).  Observe() is a few
		// interlocked ops, safe from any thread.  Percentiles are approximate: a value is reported as the UPPER BOUND of its
		// power-of-two bucket, so p50/p90/p99 can overstate by at most 2x.  min/max/mean are exact.
		public class MetricsHistogram
		{
			private readonly long[] _buckets = new long[65];  // bucket 0 = value 0; bucket i = values in [2^(i-1), 2^i - 1]
			private long _count = 0;
			private long _sum   = 0;
			private long _min   = long.MaxValue;
			private long _max   = long.MinValue;

			public void Observe(long value)
			{
				if (value<0)
					value = 0;
				int idx = (value==0) ? 0 : BitOperations.Log2((ulong)value) + 1;
				Interlocked.Increment(ref _buckets[idx]);
				Interlocked.Increment(ref _count);
				Interlocked.Add(ref _sum, value);
				long seen;
				while (value < (seen = Interlocked.Read(ref _min)) && Interlocked.CompareExchange(ref _min, value, seen)!=seen) { }
				while (value > (seen = Interlocked.Read(ref _max)) && Interlocked.CompareExchange(ref _max, value, seen)!=seen) { }
			}

			public long Count => Interlocked.Read(ref _count);
			public long Sum   => Interlocked.Read(ref _sum);
			public long Min   => Count==0 ? 0 : Interlocked.Read(ref _min);
			public long Max   => Count==0 ? 0 : Interlocked.Read(ref _max);
			public long Mean  => Count==0 ? 0 : Sum / Count;

			// Approximate: returns the upper bound of the bucket containing the q-quantile value (0.0 < q <= 1.0).
			public long Percentile(double q)
			{
				long count = Count;
				if (count==0)
					return 0;
				long target = (long)Math.Ceiling(q * count);
				long cumulative = 0;
				for (int i=0; i<_buckets.Length; i++)
				{
					cumulative += Interlocked.Read(ref _buckets[i]);
					if (cumulative>=target)
						return (i==0) ? 0 : Math.Min((1L<<i) - 1, Max);  // clamp so a bucket's upper bound can never exceed the true max
				}
				return Max;
			}

			// One line: the shape of the distribution at a glance.
			public string Report()
			{
				if (Count==0)
					return "count=0";
				return $"count={Count} min={Min} mean={Mean} p50={Percentile(0.50)} p90={Percentile(0.90)} p99={Percentile(0.99)} max={Max}";
			}
		}

		//-------------------
		// Server-wide rollup, owned by WebSocketServer and fed automatically.  Read it live any time (e.g. from a /metrics
		// endpoint handler) -- everything is interlocked, snapshots are approximate but never torn per-field.
		public class WebSocketServerMetrics
		{
			private long   _totalAccepted = 0;
			private long   _currentConnections = 0;
			private long   _highWaterConnections = 0;
			private readonly long[] _disconnectCounts = new long[16];  // indexed by EDisconnectReason
			private IDataCollection? _dc = null;                       // optional external sink (prometheus etc); events are pushed as they happen

			// Prometheus-style metric names, precomputed so the hot paths never build strings.
			private const string kGaugeCurrent    = "rgws_connections_current";
			private const string kGaugeHighWater  = "rgws_connections_high_water";
			private const string kCounterAccepted = "rgws_connections_accepted_total";
			private const string kHistoInbound    = "rgws_inbound_message_bytes";
			private const string kHistoSentMsgs   = "rgws_socket_sent_messages";
			private const string kHistoRecvMsgs   = "rgws_socket_recv_messages";
			private const string kHistoSentBytes  = "rgws_socket_sent_bytes";
			private const string kHistoRecvBytes  = "rgws_socket_recv_bytes";
			private const string kHistoDuration   = "rgws_connection_duration_seconds";
			static private readonly string[] kDisconnectCounterNames = BuildDisconnectCounterNames();  // indexed by EDisconnectReason
			static private string[] BuildDisconnectCounterNames()
			{
				string[] names = Enum.GetNames(typeof(EDisconnectReason));
				string[] ret = new string[names.Length];
				for (int i=0; i<names.Length; i++)
				{
					StringBuilder sb = new StringBuilder(64);
					sb.Append("rgws_disconnects_");
					foreach (char c in names[i])  // CamelCase -> snake_case
					{
						if (char.IsUpper(c) && sb[sb.Length-1]!='_')
							sb.Append('_');
						sb.Append(char.ToLowerInvariant(c));
					}
					sb.Append("_total");
					ret[i] = sb.ToString();
				}
				return ret;
			}

			// Register everything with an external collector (prometheus etc) and start pushing events into it.
			// Call once, before the server starts accepting connections.
			public void AttachDataCollection(IDataCollection dc)
			{
				if (dc==null)
					throw new ArgumentNullException(nameof(dc));
				dc.CreateGauge(kGaugeCurrent, "Websockets currently connected");
				dc.CreateGauge(kGaugeHighWater, "Most websockets ever connected at once");
				dc.CreateCounter(kCounterAccepted, "Total websocket connections accepted");
				for (int i=0; i<kDisconnectCounterNames.Length; i++)
					dc.CreateCounter(kDisconnectCounterNames[i], $"Websocket disconnects caused by {(EDisconnectReason)i}");
				dc.CreateHistogram(kHistoInbound, "Inbound websocket message payload bytes", new double[] { 16, 64, 256, 1024, 4096, 16384, 65536, 262144, 1048576, 4194304 });
				dc.CreateHistogram(kHistoSentMsgs, "Messages sent to one socket over its lifetime", new double[] { 1, 10, 100, 1000, 10000, 100000 });
				dc.CreateHistogram(kHistoRecvMsgs, "Messages received from one socket over its lifetime", new double[] { 1, 10, 100, 1000, 10000, 100000 });
				dc.CreateHistogram(kHistoSentBytes, "Bytes sent to one socket over its lifetime", new double[] { 1024, 16384, 262144, 4194304, 67108864, 1073741824 });
				dc.CreateHistogram(kHistoRecvBytes, "Bytes received from one socket over its lifetime", new double[] { 1024, 16384, 262144, 4194304, 67108864, 1073741824 });
				dc.CreateHistogram(kHistoDuration, "Websocket connection lifetime in seconds", new double[] { 1, 10, 60, 600, 3600, 21600, 86400 });
				dc.SetGauge(kGaugeCurrent, 0);
				dc.SetGauge(kGaugeHighWater, 0);
				_dc = dc;
			}

			public MetricsHistogram InboundMsgBytes      { get; } = new MetricsHistogram();  // per-message inbound payload size
			public MetricsHistogram PerSocketSentMsgs    { get; } = new MetricsHistogram();  // lifetime message counts, observed per socket at disconnect
			public MetricsHistogram PerSocketRecvMsgs    { get; } = new MetricsHistogram();
			public MetricsHistogram PerSocketSentBytes   { get; } = new MetricsHistogram();
			public MetricsHistogram PerSocketRecvBytes   { get; } = new MetricsHistogram();
			public MetricsHistogram ConnectionDurationMS { get; } = new MetricsHistogram();

			public long CurrentConnections   => Interlocked.Read(ref _currentConnections);
			public long HighWaterConnections => Interlocked.Read(ref _highWaterConnections);
			public long TotalAccepted        => Interlocked.Read(ref _totalAccepted);
			public long GetDisconnectCount(EDisconnectReason reason) { return Interlocked.Read(ref _disconnectCounts[(int)reason]); }

			public void RecordConnect()
			{
				Interlocked.Increment(ref _totalAccepted);
				long current = Interlocked.Increment(ref _currentConnections);
				long seen;
				bool raisedHighWater = false;
				while (current > (seen = Interlocked.Read(ref _highWaterConnections)))
				{
					if (Interlocked.CompareExchange(ref _highWaterConnections, current, seen)==seen)
					{
						raisedHighWater = true;
						break;
					}
				}
				if (_dc!=null)
				{
					_dc.IncrementCounter(kCounterAccepted, 1);
					_dc.SetGauge(kGaugeCurrent, current);
					if (raisedHighWater)
						_dc.SetGauge(kGaugeHighWater, current);
				}
			}

			// Called once per message received, from the socket's recv task.
			public void RecordInboundMessage(long payloadBytes)
			{
				InboundMsgBytes.Observe(payloadBytes);
				_dc?.ObserveHistogram(kHistoInbound, payloadBytes);
			}

			// Called once per socket when it disconnects; folds the socket's lifetime stats into the distributions.
			public void RecordDisconnect(RGWebSocket rgws)
			{
				long current = Interlocked.Decrement(ref _currentConnections);
				Interlocked.Increment(ref _disconnectCounts[(int)rgws.DisconnectReason]);
				long durationMS = (long)TimeSpan.FromTicks(DateTime.UtcNow.Ticks - rgws.ConnectedAtTicks).TotalMilliseconds;
				PerSocketSentMsgs.Observe(rgws.SentMessages);
				PerSocketRecvMsgs.Observe(rgws.RecvMessages);
				PerSocketSentBytes.Observe(rgws.SentBytes);
				PerSocketRecvBytes.Observe(rgws.RecvBytes);
				ConnectionDurationMS.Observe(durationMS);
				if (_dc!=null)
				{
					_dc.SetGauge(kGaugeCurrent, current);
					_dc.IncrementCounter(kDisconnectCounterNames[(int)rgws.DisconnectReason], 1);
					_dc.ObserveHistogram(kHistoSentMsgs, rgws.SentMessages);
					_dc.ObserveHistogram(kHistoRecvMsgs, rgws.RecvMessages);
					_dc.ObserveHistogram(kHistoSentBytes, rgws.SentBytes);
					_dc.ObserveHistogram(kHistoRecvBytes, rgws.RecvBytes);
					_dc.ObserveHistogram(kHistoDuration, durationMS / 1000.0);  // prometheus convention is base units (seconds)
				}
			}

			public string Report()
			{
				StringBuilder sb = new StringBuilder(1024);
				sb.AppendLine($"connections:          current={CurrentConnections} highWater={HighWaterConnections} totalAccepted={TotalAccepted}");
				sb.Append("disconnects:         ");
				for (int i=0; i<_disconnectCounts.Length; i++)
				{
					long c = Interlocked.Read(ref _disconnectCounts[i]);
					if (c>0)
						sb.Append($" {(EDisconnectReason)i}={c}");
				}
				sb.AppendLine();
				sb.AppendLine($"inboundMsgBytes:      {InboundMsgBytes.Report()}");
				sb.AppendLine($"perSocketSentMsgs:    {PerSocketSentMsgs.Report()}");
				sb.AppendLine($"perSocketRecvMsgs:    {PerSocketRecvMsgs.Report()}");
				sb.AppendLine($"perSocketSentBytes:   {PerSocketSentBytes.Report()}");
				sb.AppendLine($"perSocketRecvBytes:   {PerSocketRecvBytes.Report()}");
				sb.Append($"connectionDurationMS: {ConnectionDurationMS.Report()}");
				return sb.ToString();
			}
		}
	}
}
