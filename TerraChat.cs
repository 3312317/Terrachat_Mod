using Microsoft.Xna.Framework.Input;
using System.IO;
using TerraChat.Common;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerraChat
{
	public class TerraChat : Mod
	{
		internal const int MaximumNetworkAvatarBytes = 60 * 1024;
		internal static ModKeybind ToggleChatKeybind { get; private set; }
		private static long serverClockOffsetTicks;

		public override void Load()
		{
			serverClockOffsetTicks = System.DateTime.Now.Ticks - System.DateTime.UtcNow.Ticks;
			ToggleChatKeybind = KeybindLoader.RegisterKeybind(this, "ToggleChatUI", Keys.C);
		}

		public override void Unload()
		{
			serverClockOffsetTicks = 0;
			ToggleChatKeybind = null;
		}

		public override void HandlePacket(BinaryReader reader, int whoAmI)
		{
			TerraChatPacketType packetType = (TerraChatPacketType)reader.ReadByte();
			if (packetType == TerraChatPacketType.ServerTimeSync)
			{
				if (Main.netMode == NetmodeID.MultiplayerClient)
				{
					UpdateServerClock(reader.ReadInt64());
				}
				return;
			}
			if (packetType != TerraChatPacketType.ProfileSync)
			{
				return;
			}

			int playerId = reader.ReadByte();
			string profileName = reader.ReadString();
			string description = reader.ReadString();
			int avatarLength = reader.ReadUInt16();
			if (avatarLength > MaximumNetworkAvatarBytes)
			{
				throw new InvalidDataException("TerraChat profile avatar exceeds the network limit.");
			}
			byte[] avatarData = reader.ReadBytes(avatarLength);
			if (avatarData.Length != avatarLength)
			{
				throw new EndOfStreamException();
			}
			long reportedServerTimeTicks = reader.ReadInt64();

			if (Main.netMode == NetmodeID.Server)
			{
				playerId = whoAmI;
			}
			if (playerId < 0 || playerId >= Main.maxPlayers)
			{
				return;
			}

			TerraChatPlayer profile = Main.player[playerId].GetModPlayer<TerraChatPlayer>();
			profile.SetNetworkProfile(profileName, description, avatarData);
			if (Main.netMode == NetmodeID.Server)
			{
				SendProfile(playerId, -1, whoAmI);
				SendServerTime(whoAmI);
			}
			else
			{
				UpdateServerClock(reportedServerTimeTicks);
				TerraChatUISystem.UpdateNetworkAvatar(playerId, avatarData);
			}
		}

		internal static void SendProfile(int playerId, int toWho = -1, int ignoreClient = -1)
		{
			if (Main.netMode == NetmodeID.SinglePlayer || playerId < 0 || playerId >= Main.maxPlayers)
			{
				return;
			}

			TerraChatPlayer profile = Main.player[playerId].GetModPlayer<TerraChatPlayer>();
			byte[] avatarData = profile.NetworkAvatarData ?? [];
			int avatarLength = System.Math.Min(avatarData.Length, MaximumNetworkAvatarBytes);
			ModPacket packet = ModContent.GetInstance<TerraChat>().GetPacket();
			packet.Write((byte)TerraChatPacketType.ProfileSync);
			packet.Write((byte)playerId);
			packet.Write(profile.ProfileName ?? string.Empty);
			packet.Write(profile.Description ?? string.Empty);
			packet.Write((ushort)avatarLength);
			packet.Write(avatarData, 0, avatarLength);
			packet.Write(System.DateTime.Now.Ticks);
			packet.Send(toWho, ignoreClient);
		}

		internal static string GetChatTimestamp()
		{
			if (Main.netMode != NetmodeID.MultiplayerClient)
			{
				return System.DateTime.Now.ToString("yyyy-MM-dd HH:mm");
			}

			long synchronizedTicks = System.DateTime.UtcNow.Ticks + serverClockOffsetTicks;
			if (synchronizedTicks < System.DateTime.MinValue.Ticks || synchronizedTicks > System.DateTime.MaxValue.Ticks)
			{
				return System.DateTime.Now.ToString("yyyy-MM-dd HH:mm");
			}
			return new System.DateTime(synchronizedTicks, System.DateTimeKind.Unspecified).ToString("yyyy-MM-dd HH:mm");
		}

		private static void UpdateServerClock(long serverTimeTicks)
		{
			if (serverTimeTicks < System.DateTime.MinValue.Ticks || serverTimeTicks > System.DateTime.MaxValue.Ticks)
			{
				return;
			}
			serverClockOffsetTicks = serverTimeTicks - System.DateTime.UtcNow.Ticks;
		}

		private static void SendServerTime(int toWho)
		{
			if (Main.netMode != NetmodeID.Server || toWho < 0 || toWho >= Main.maxPlayers)
			{
				return;
			}

			ModPacket packet = ModContent.GetInstance<TerraChat>().GetPacket();
			packet.Write((byte)TerraChatPacketType.ServerTimeSync);
			packet.Write(System.DateTime.Now.Ticks);
			packet.Send(toWho);
		}

		private enum TerraChatPacketType : byte
		{
			ProfileSync,
			ServerTimeSync
		}
	}
}
