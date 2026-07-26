//-------------------
// Reachable Games
// Copyright 2026
//-------------------
// Tiny dependency-free test harness, in the ChatTest mold: no frameworks, sequential groups, loud failures,
// and the process exit code is the verdict.  A test fails by throwing Expect.TestFailure (what the Expect
// helpers throw); anything ELSE escaping a test is reported as ERROR -- a bug in the code under test or in
// the test itself, never a soft failure.

using Logging;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ReachableGames
{
	namespace RGWebSocket
	{
		namespace UnitTests
		{
			// Console logger that counts Error lines.  Several tests deliberately provoke Error logs (breakers, protocol
			// violations), so errors are counted and reported rather than failed on.
			public class TestLogger : ILogging
			{
				private object _lock   = new object();
				private int    _errors = 0;

				public EVerbosity Verbosity { get; set; }
				public int        ErrorCount => _errors;

				public TestLogger(EVerbosity verbosity)
				{
					Verbosity = verbosity;
				}

				public void Log(EVerbosity level, string msg)
				{
					if (level == EVerbosity.Error)
						Interlocked.Increment(ref _errors);
					if (level > Verbosity)
						return;
					lock (_lock)
						Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}][{level}] {msg}");
				}

				public void Dispose()
				{
				}
			}

			//-------------------
			// Assertion helpers.  Every failure message says what was being checked, not just that two values differed.
			static public class Expect
			{
				public class TestFailure : Exception
				{
					public TestFailure(string msg) : base(msg) { }
				}

				static public void True(bool condition, string what)
				{
					if (condition == false)
						throw new TestFailure(what);
				}

				static public void Eq<T>(T expected, T actual, string what)
				{
					if (EqualityComparer<T>.Default.Equals(expected, actual) == false)
						throw new TestFailure($"{what} -- expected [{expected}] got [{actual}]");
				}

				static public TEx Throws<TEx>(Action fn, string what) where TEx : Exception
				{
					try
					{
						fn();
					}
					catch (TEx e)
					{
						return e;
					}
					catch (Exception e)
					{
						throw new TestFailure($"{what} -- expected {typeof(TEx).Name}, got {e.GetType().Name}: {e.Message}");
					}
					throw new TestFailure($"{what} -- expected {typeof(TEx).Name}, nothing was thrown");
				}

				static public async Task<TEx> ThrowsAsync<TEx>(Func<Task> fn, string what) where TEx : Exception
				{
					try
					{
						await fn().ConfigureAwait(false);
					}
					catch (TEx e)
					{
						return e;
					}
					catch (Exception e)
					{
						throw new TestFailure($"{what} -- expected {typeof(TEx).Name}, got {e.GetType().Name}: {e.Message}");
					}
					throw new TestFailure($"{what} -- expected {typeof(TEx).Name}, nothing was thrown");
				}

				// Poll until the condition holds or the deadline passes.  For the async seams (disconnect callbacks,
				// reaper drains) where "eventually" is the contract, not "immediately".
				static public async Task Within(int timeoutMs, Func<bool> condition, string what)
				{
					long deadline = Environment.TickCount64 + timeoutMs;
					while (Environment.TickCount64 < deadline)
					{
						if (condition())
							return;
						await Task.Delay(10).ConfigureAwait(false);
					}
					if (condition() == false)
						throw new TestFailure($"{what} -- still not true after {timeoutMs}ms");
				}
			}

			//-------------------
			// Sequential test runner.  Groups run in the order Program calls them, tests run in the order given --
			// ordering is deliberate here (config freezing, pool baselines), not a smell.
			static public class Runner
			{
				static private int          _passed   = 0;
				static private List<string> _failures = new List<string>();

				static public int                   Passed   => _passed;
				static public IReadOnlyList<string> Failures => _failures;

				static public async Task Group(string group, params (string name, Func<Task> body)[] tests)
				{
					Console.WriteLine($"--- {group} ---");
					foreach ((string name, Func<Task> body) in tests)
					{
						try
						{
							await body().ConfigureAwait(false);
							_passed++;
							Console.WriteLine($"  PASS  {name}");
						}
						catch (Expect.TestFailure f)
						{
							_failures.Add($"{group} / {name}: {f.Message}");
							Console.WriteLine($"  FAIL  {name}: {f.Message}");
						}
						catch (Exception e)
						{
							_failures.Add($"{group} / {name}: ERROR {e.GetType().Name}: {e.Message}");
							Console.WriteLine($"  ERROR {name}: {e}");
						}
					}
				}
			}
		}
	}
}