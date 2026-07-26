#nullable enable
//-------------------
// Reachable Games
// Copyright 2023
//-------------------

using System;

//-------------------

namespace ReachableGames
{
	namespace RGWebSocket
	{
		static public class Utilities
		{
			// Human-readable formatted string, from StackOverflow
			static public String BytesToHumanReadable(long byteCount)
			{
				string[] suf = { "B", "KB", "MB", "GB", "TB", "PB", "EB" }; //Longs run out around EB
				if (byteCount == 0)
					return "0" + suf[0];
				long   bytes = byteCount == long.MinValue ? long.MaxValue : Math.Abs(byteCount); // Math.Abs(long.MinValue) throws; one byte of display error beats an exception in a formatting helper
				int    place = Convert.ToInt32(Math.Floor(Math.Log(bytes, 1024)));
				double num   = Math.Round(bytes / Math.Pow(1024, place), 1);
				return (Math.Sign(byteCount) * num).ToString("F1") + suf[place];
			}
		}
	}
}