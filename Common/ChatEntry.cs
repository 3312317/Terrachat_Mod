namespace TerraChat.Common
{
	internal sealed class ChatEntry
	{
		internal string Sender { get; }
		internal string Text { get; }
		internal string Time { get; }
		internal bool IsLocalPlayer { get; }
		internal int SenderPlayerId { get; }

		internal ChatEntry(string sender, string text, string time, bool isLocalPlayer, int senderPlayerId)
		{
			Sender = sender;
			Text = text;
			Time = time;
			IsLocalPlayer = isLocalPlayer;
			SenderPlayerId = senderPlayerId;
		}
	}
}
