//-------------------
// Reachable Games
// Copyright 2026
//-------------------
// Minimal IDataCollection implementation for the stress test: proves the library registers and pushes metrics correctly.
// A real derivative would wrap prometheus-net (or similar); this one just accumulates so the test can assert on it.

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using DataCollection;

namespace ReachableGames
{
	namespace RGWebSocket
	{
		namespace ChatTest
		{
			public class TestDataCollection : IDataCollection
			{
				private readonly object _lock = new object();
				private readonly Dictionary<string, double> _gauges = new Dictionary<string, double>();
				private readonly Dictionary<string, double> _counters = new Dictionary<string, double>();
				private readonly Dictionary<string, long>   _histogramCounts = new Dictionary<string, long>();
				private readonly Dictionary<string, double> _histogramSums = new Dictionary<string, double>();

				public void CreateGauge(string gaugeName, string description)     { lock (_lock) _gauges[gaugeName] = 0; }
				public void SetGauge(string gaugeName, double value)              { lock (_lock) _gauges[gaugeName] = value; }
				public void CreateCounter(string counterName, string description) { lock (_lock) _counters[counterName] = 0; }
				public void IncrementCounter(string counterName, double v)        { lock (_lock) _counters[counterName] = _counters.TryGetValue(counterName, out double c) ? c + v : v; }

				public void CreateHistogram(string histogramName, string description, double[] bucketUpperBounds)
				{
					lock (_lock)
					{
						_histogramCounts[histogramName] = 0;
						_histogramSums[histogramName] = 0;
					}
				}

				public void ObserveHistogram(string histogramName, double value)
				{
					lock (_lock)
					{
						_histogramCounts[histogramName] = _histogramCounts.TryGetValue(histogramName, out long c) ? c + 1 : 1;
						_histogramSums[histogramName] = _histogramSums.TryGetValue(histogramName, out double s) ? s + value : value;
					}
				}

				public Task<byte[]> Generate()
				{
					StringBuilder sb = new StringBuilder(2048);
					lock (_lock)
					{
						foreach (KeyValuePair<string, double> kvp in _gauges)
							sb.AppendLine($"{kvp.Key} {kvp.Value}");
						foreach (KeyValuePair<string, double> kvp in _counters)
							sb.AppendLine($"{kvp.Key} {kvp.Value}");
						foreach (KeyValuePair<string, long> kvp in _histogramCounts)
							sb.AppendLine($"{kvp.Key}_count {kvp.Value}");
					}
					return Task.FromResult(Encoding.UTF8.GetBytes(sb.ToString()));
				}

				public void Dispose()
				{
				}

				// Test accessors
				public double GetGauge(string name)          { lock (_lock) return _gauges.TryGetValue(name, out double v) ? v : double.NaN; }
				public double GetCounter(string name)        { lock (_lock) return _counters.TryGetValue(name, out double v) ? v : double.NaN; }
				public long   GetHistogramCount(string name) { lock (_lock) return _histogramCounts.TryGetValue(name, out long v) ? v : -1; }
			}
		}
	}
}
