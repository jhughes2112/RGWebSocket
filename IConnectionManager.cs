using System.Threading.Tasks;

namespace ReachableGames
{
	namespace RGWebSocket
	{
		// IConnectionManager allows you to implement a class that manages a bunch of websocket connections.
		// Each of the callbacks can be async or not.  If not, just return Task.CompletedTask.
		// The rationale for a single object that manages many connections is, generally you will have to look up
		// the state of a connection and do something with the messages that you service, and there's a certain amount
		// of central bookkeeping that must exist for that, plus the message dictionaries are usually the same for all
		// connections, so it reduces overhead to put them in one place.  This neatly gives you a place to implement that.
		public interface IConnectionManager
		{
			Task OnConnection(RGWebSocket rgws);
			Task OnDisconnect(RGWebSocket rgws);
			Task OnReceiveText(RGWebSocket rgws, string msg);
			Task OnReceiveBinary(RGWebSocket rgws, byte[] msg);
		}
	}
}