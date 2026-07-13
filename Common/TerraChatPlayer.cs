using System.Collections.Generic;
using System.Linq;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.ModLoader.IO;

namespace TerraChat.Common
{
	internal sealed class TerraChatPlayer : ModPlayer
	{
		internal const int MaximumMessages = 200;
		internal const int MaximumProfileNameLength = 32;
		internal const int MaximumDescriptionLength = 200;
		internal readonly List<ChatEntry> History = new();
		internal string ProfileName = string.Empty;
		internal string Description = string.Empty;
		internal byte[] NetworkAvatarData = [];

		internal string GetDisplayName()
		{
			return string.IsNullOrWhiteSpace(ProfileName) ? Player.name : ProfileName;
		}

		internal void AddMessage(ChatEntry entry)
		{
			History.Add(entry);
			if (History.Count > MaximumMessages)
			{
				History.RemoveAt(0);
			}
		}

		internal void SetNetworkProfile(string profileName, string description, byte[] avatarData)
		{
			ProfileName = (profileName ?? string.Empty).Trim();
			Description = description ?? string.Empty;
			if (ProfileName.Length > MaximumProfileNameLength)
			{
				ProfileName = ProfileName.Substring(0, MaximumProfileNameLength);
			}
			if (Description.Length > MaximumDescriptionLength)
			{
				Description = Description.Substring(0, MaximumDescriptionLength);
			}
			NetworkAvatarData = avatarData ?? [];
		}

		public override void OnEnterWorld()
		{
			if (Player.whoAmI == Main.myPlayer)
			{
				TerraChatUISystem.RequestLocalProfileSync();
			}
		}

		public override void SyncPlayer(int toWho, int fromWho, bool newPlayer)
		{
			if (Main.netMode == NetmodeID.Server)
			{
				TerraChat.SendProfile(Player.whoAmI, toWho, fromWho);
			}
		}

		public override void SaveData(TagCompound tag)
		{
			tag["profileName"] = ProfileName;
			tag["description"] = Description;
			tag["history"] = History.Select(entry => new TagCompound
			{
				["sender"] = entry.Sender,
				["text"] = entry.Text,
				["time"] = entry.Time,
				["local"] = entry.IsLocalPlayer,
				["playerId"] = entry.SenderPlayerId
			}).ToList();
		}

		public override void LoadData(TagCompound tag)
		{
			ProfileName = tag.GetString("profileName");
			Description = tag.GetString("description");
			if (ProfileName.Length > MaximumProfileNameLength)
			{
				ProfileName = ProfileName.Substring(0, MaximumProfileNameLength);
			}
			if (Description.Length > MaximumDescriptionLength)
			{
				Description = Description.Substring(0, MaximumDescriptionLength);
			}
			History.Clear();
			foreach (TagCompound entryTag in tag.GetList<TagCompound>("history").Take(MaximumMessages))
			{
				History.Add(new ChatEntry(
					entryTag.GetString("sender"),
					entryTag.GetString("text"),
					entryTag.GetString("time"),
					entryTag.GetBool("local"),
					entryTag.ContainsKey("playerId") ? entryTag.GetInt("playerId") : -1));
			}
		}
	}
}
