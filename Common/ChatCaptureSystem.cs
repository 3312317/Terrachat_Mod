using Microsoft.Xna.Framework;
using Terraria;
using Terraria.Chat;
using Terraria.Localization;
using Terraria.ModLoader;

namespace TerraChat.Common
{
	[Autoload(Side = ModSide.Client)]
	internal sealed class ChatCaptureSystem : ModSystem
	{
		public override void Load()
		{
			On_ChatHelper.DisplayMessage += CapturePlayerMessage;
		}

		public override void Unload()
		{
			On_ChatHelper.DisplayMessage -= CapturePlayerMessage;
		}

		private static void CapturePlayerMessage(
			On_ChatHelper.orig_DisplayMessage original,
			NetworkText text,
			Color color,
			byte messageAuthor)
		{
			original(text, color, messageAuthor);

			if (messageAuthor >= Main.maxPlayers || Main.gameMenu)
			{
				return;
			}

			Player sender = Main.player[messageAuthor];
			string message = text.ToString().Trim();
			if (string.IsNullOrEmpty(message) || string.IsNullOrEmpty(sender.name))
			{
				return;
			}

			TerraChatPlayer chatPlayer = Main.LocalPlayer.GetModPlayer<TerraChatPlayer>();
			TerraChatPlayer senderProfile = sender.GetModPlayer<TerraChatPlayer>();
			chatPlayer.AddMessage(new ChatEntry(
				messageAuthor == Main.myPlayer ? chatPlayer.GetDisplayName() : senderProfile.GetDisplayName(),
				message,
				TerraChat.GetChatTimestamp(),
				messageAuthor == Main.myPlayer,
				messageAuthor));
			TerraChatUISystem.NotifyMessageAdded();
		}
	}
}
