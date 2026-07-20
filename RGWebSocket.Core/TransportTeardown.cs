using System;

namespace ReachableGames
{
	namespace RGWebSocket
	{
		//-------------------
		// THE TEARDOWN CONTRACT -- the one place that decides which exceptions are the normal end of a connection.
		//
		// A peer that vanishes mid-operation (closed its tab, lost its network, crashed) or a listener tearing down its
		// shared handles during shutdown surfaces as an exception from whatever send/recv/close/handle call was in flight.
		// That is the NORMAL end of a connection, not a fault: it happens all day in healthy operation, it is not
		// actionable, and logging it loudly buries real errors.  Every class that touches the transport classifies those
		// exceptions through IsExpected() below and follows the same three rules:
		//
		// 1. AVOID where a state check can.  If the API is only valid from certain states, check the state and skip the
		//    call instead of relying on the catch -- e.g. CloseOutputAsync is only sent from Open/CloseReceived
		//    (RGWebSocket's Send task).  Avoidance beats squelching because the catch then only sees genuine races.
		//
		// 2. SQUELCH what a race can still throw.  A catch filtered by IsExpected() logs at Debug (never Error, no
		//    stack) and carries on with teardown.  RGWebSocket additionally records the detail on LastError via
		//    NoteQuietDisconnect so a post-mortem can still see why the socket died.
		//
		// 3. Everything else stays LOUD.  An exception type not listed here is a real bug somewhere and gets the full
		//    Error+stack treatment.  Never widen a squelch to catch-all "to be safe" -- that is how bugs hide.
		//    (One deliberate exception: RGWebSocket's Recv task squelches ALL ReceiveAsync failures, because a failed
		//    receive is terminal for the socket by definition and the disconnect reason is already stamped.)
		//
		// Cancellation is NOT teardown noise and is not classified here: OperationCanceledException is flow control
		// (the socket's own cancellation, a handler timeout, or the app's shutdown token) and every transport loop
		// gives it its own silent catch.  Note the app shutdown tokens (the matchmaker's and game server's _tokenSrc)
		// deliberately play no part in classification: these exception types are just as expected when a single client
		// hangs up mid-reply on a healthy server, so "is the process shutting down" cannot distinguish expected from
		// unexpected -- the exception type alone does.  Shutdown/draining order is the app layer's job; by the time it
		// reaches the transport it looks like cancellation (rule above) or a disposed handle (listed below).
		internal static class TransportTeardown
		{
			// The expected types, and what throws them:
			//   ObjectDisposedException  -- the socket/response/listener handle was disposed mid-operation; most commonly
			//                               the server's listener disposing its shared IOCP handle (ThreadPoolBoundHandle)
			//                               during shutdown, or HttpListener disposing a response after auto-411ing.
			//   WebSocketException       -- the peer closed/reset without completing the websocket handshake or close.
			//   IOException              -- the underlying stream/pipe broke mid-read/write.
			//   HttpListenerException    -- the listener rejected/aborted the operation because the connection is gone.
			//   SocketException          -- the OS socket reset/aborted (peer vanished, network dropped).
			internal static bool IsExpected(Exception e)
			{
				return e is ObjectDisposedException
					|| e is System.Net.WebSockets.WebSocketException
					|| e is System.IO.IOException
					|| e is System.Net.HttpListenerException
					|| e is System.Net.Sockets.SocketException;
			}
		}
	}
}
