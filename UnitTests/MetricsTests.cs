//-------------------
// Reachable Games
// Copyright 2026
//-------------------
// MetricsHistogram math and WebSocketServerMetrics bookkeeping -- including the contract that a THROWING
// IDataCollection sink is counted and contained, never allowed to escape into the teardown paths it used to wedge.

using DataCollection;
using Logging;
using System;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace ReachableGames
{
	namespace RGWebSocket
	{
		namespace UnitTests
		{
			// A sink that behaves during Attach (Create*/initial SetGauge), then throws on every later push once armed.
			public class FaultySink : IDataCollection
			{
				public bool         Armed = false;
				public void         CreateGauge     (string name, string description)                             { }
				public void         CreateCounter   (string name, string description)                             { }
				public void         CreateHistogram (string name, string description, double[] bucketUpperBounds) { }
				public void         SetGauge        (string name, double value)                                   { if (Armed) throw new InvalidOperationException("sink fault"); }
				public void         IncrementCounter(string name,     double v)                                   { if (Armed) throw new InvalidOperationException("sink fault"); }
				public void         ObserveHistogram(string name, double value)                                   { if (Armed) throw new InvalidOperationException("sink fault"); }
				public Task<byte[]> Generate        () { return Task.FromResult(Array.Empty<byte>()); }
				public void         Dispose         () { }
			}

			static public class MetricsTests
			{
				// A never-started RGWebSocket is a legitimate stats carrier for RecordDisconnect.
				static private RGWebSocket MakeDeadSocket(ILogging logger)
				{
					return new RGWebSocket(null, (rgws, msg, isText) => Task.CompletedTask, (rgws) => Task.CompletedTask, logger, "test-socket", new ClientWebSocket());
				}

				static public Task Run(TestLogger logger)
				{
					return Runner.Group("Metrics",
						("histogram: empty state", () =>
						{
							MetricsHistogram h = new MetricsHistogram();
							Expect.Eq(0L, h.Count, "count");
							Expect.Eq(       0L,             h.Min, "min of nothing is 0, not long.MaxValue");
							Expect.Eq(       0L,             h.Max, "max of nothing is 0, not long.MinValue");
							Expect.Eq(       0L,            h.Mean, "mean of nothing");
							Expect.Eq(       0L, h.Percentile(0.5), "percentile of nothing");
							Expect.Eq("count=0",        h.Report(), "report of nothing");
							return Task.CompletedTask;
						}
					),
						("histogram: known values land in the right buckets", () =>
						{
							MetricsHistogram h = new MetricsHistogram();
							h.Observe(1);
							h.Observe(2);
							h.Observe(3);
							h.Observe(100);
							Expect.Eq(  4L, h.Count, "count");
							Expect.Eq(106L,   h.Sum, "sum");
							Expect.Eq(  1L,   h.Min, "min");
							Expect.Eq(100L,   h.Max, "max is exact");
							Expect.Eq( 26L,  h.Mean, "mean");
							Expect.Eq(  3L, h.Percentile(0.50), "p50 reports its bucket's upper bound (values 2,3 share bucket [2,3])");
							Expect.Eq(100L, h.Percentile(1.00), "p100 is clamped to the true max, not the bucket bound 127");
							return Task.CompletedTask;
						}
					),
						("histogram: zero and negative observations", () =>
						{
							MetricsHistogram h = new MetricsHistogram();
							h.Observe(0);
							h.Observe(-5); // clamped to 0
							Expect.Eq(2L,            h.Count, "both counted");
							Expect.Eq(0L,              h.Sum, "negative clamped to zero");
							Expect.Eq(0L, h.Percentile(0.99), "everything in the zero bucket");
							h.Observe(long.MaxValue); // top bucket must not blow up
							Expect.Eq(long.MaxValue, h.Max, "max handles the extreme");
							return Task.CompletedTask;
						}
					),
						("histogram: concurrent observers stay exact on count and sum", async () =>
						{
							MetricsHistogram h = new MetricsHistogram();
							const int kTasks = 8, kPer = 100000;
							Task[] tasks = new Task[kTasks];
							for (int t = 0; t < kTasks; t++)
								tasks[t] = Task.Run(() => { for (int i = 0; i < kPer; i++) h.Observe(i % 1000); });
							await Task.WhenAll(tasks).ConfigureAwait(false);
							Expect.Eq((long)kTasks * kPer, h.Count, "count exact");
							Expect.Eq((long)kTasks * 499500L * (kPer / 1000), h.Sum, "sum exact (each task contributes sum 0..999, kPer/1000 times)");
							Expect.Eq(  0L, h.Min, "min exact");
							Expect.Eq(999L, h.Max, "max exact");
						}
					),
						("server metrics: connect/disconnect bookkeeping and high water", async () =>
						{
							WebSocketServerMetrics m = new WebSocketServerMetrics();
							m.RecordConnect();
							m.RecordConnect();
							m.RecordConnect();
							Expect.Eq(3L,   m.CurrentConnections, "current after three connects");
							Expect.Eq(3L, m.HighWaterConnections, "high water");
							Expect.Eq(3L,        m.TotalAccepted, "total");
							RGWebSocket dead = MakeDeadSocket(logger);
							m.RecordDisconnect(dead);
							Expect.Eq(2L,                         m.CurrentConnections, "current after a disconnect");
							Expect.Eq(3L,                       m.HighWaterConnections, "high water never falls");
							Expect.Eq(1L, m.GetDisconnectCount(EDisconnectReason.None), "never-started socket lands under None");
							await dead.Shutdown().ConfigureAwait(false);
						}
					),
						("server metrics: disconnect counters cover every enum value", () =>
						{
							WebSocketServerMetrics m = new WebSocketServerMetrics();
							foreach (EDisconnectReason reason in Enum.GetValues<EDisconnectReason>())
								Expect.Eq(0L, m.GetDisconnectCount(reason), $"counter exists and reads for {reason} (the array must be sized from the enum)");
							return Task.CompletedTask;
						}
					),
						("a throwing IDataCollection sink is counted, contained, and doesn't corrupt internal metrics", async () =>
						{
							WebSocketServerMetrics m    = new WebSocketServerMetrics();
							FaultySink             sink = new FaultySink();
							m.AttachDataCollection(sink); // Attach pushes initial gauges; the sink behaves until armed
							sink.Armed       = true;
							RGWebSocket dead = MakeDeadSocket(logger);
							m.RecordConnect();          // sink throws inside
							m.RecordInboundMessage(50); // sink throws inside
							m.RecordDisconnect(dead);   // sink throws inside -- this is the one that used to strand sockets unreaped
							Expect.Eq(3L,       m.CollectorFaults, "each faulted push counted");
							Expect.Eq(1L,         m.TotalAccepted, "internal counters unaffected by the sink");
							Expect.Eq(0L,    m.CurrentConnections, "connect/disconnect balanced");
							Expect.Eq(1L, m.InboundMsgBytes.Count, "internal histogram unaffected by the sink");
							await dead.Shutdown().ConfigureAwait(false);
						}
					));
				}
			}
		}
	}
}