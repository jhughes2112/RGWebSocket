#nullable enable
﻿using System;
using System.Threading.Tasks;

namespace DataCollection
{
	public interface IDataCollection : IDisposable
	{
		void CreateGauge(string gaugeName, string description);
		void SetGauge(string gaugeName, double value);
		
		void CreateCounter(string counterName, string description);
		void IncrementCounter(string counterName, double v);

		// Histograms carry distributions (message sizes, durations, per-socket totals) so Grafana can do heatmaps and
		// histogram_quantile().  Bucket upper bounds are fixed at creation, prometheus-style; observations are per-event.
		void CreateHistogram(string histogramName, string description, double[] bucketUpperBounds);
		void ObserveHistogram(string histogramName, double value);

		// Asynchronously produce a Prometheus-compatible page of metrics
		Task<byte[]> Generate();
	}
}
