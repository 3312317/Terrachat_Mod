using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System;
using System.IO;
using System.Runtime.InteropServices;
using Terraria;

namespace TerraChat.Common
{
	internal static class AvatarStorage
	{
		internal const int AvatarTextureSize = 384;
		private static readonly string AvatarDirectory = Path.Combine(Main.SavePath, "ModConfigs", "TerraChat");
		private static readonly string AvatarPath = Path.Combine(AvatarDirectory, "avatar.png");
		private static readonly string LegacyAvatarPath = Path.Combine(AvatarDirectory, "avatar.img");
		private static readonly string LastImageDirectoryPath = Path.Combine(AvatarDirectory, "last-image-directory.txt");
		private static string lastImageDirectory = LoadLastImageDirectory();

		internal static Texture2D LoadTexture()
		{
			string path = File.Exists(AvatarPath) ? AvatarPath : LegacyAvatarPath;
			if (!File.Exists(path))
			{
				return null;
			}

			using FileStream stream = File.OpenRead(path);
			return Texture2D.FromStream(Main.graphics.GraphicsDevice, stream);
		}

		internal static IntPtr GetDialogOwnerHandle()
		{
			return OperatingSystem.IsWindows() ? GetForegroundWindow() : IntPtr.Zero;
		}

		internal static bool TryChooseImagePath(IntPtr ownerHandle, out string selectedPath)
		{
			selectedPath = null;
			if (!OperatingSystem.IsWindows())
			{
				return false;
			}

			const int fileBufferLength = 1024;
			IntPtr filterPointer = Marshal.StringToHGlobalUni("图片文件\0*.png;*.jpg;*.jpeg;*.bmp\0所有文件\0*.*\0");
			IntPtr titlePointer = Marshal.StringToHGlobalUni("选择聊天头像");
			IntPtr initialDirectoryPointer = string.IsNullOrEmpty(lastImageDirectory)
				? IntPtr.Zero
				: Marshal.StringToHGlobalUni(lastImageDirectory);
			IntPtr filePointer = Marshal.AllocHGlobal(fileBufferLength * sizeof(char));
			Marshal.WriteInt16(filePointer, 0);
			try
			{
				OpenFileName dialog = new()
				{
					StructSize = Marshal.SizeOf<OpenFileName>(),
					Owner = ownerHandle,
					Filter = filterPointer,
					File = filePointer,
					MaxFile = fileBufferLength,
					InitialDirectory = initialDirectoryPointer,
					Title = titlePointer,
					Flags = 0x00000800 | 0x00001000 | 0x00080000
				};

				if (!GetOpenFileName(ref dialog))
				{
					return false;
				}

				selectedPath = Marshal.PtrToStringUni(filePointer);
				lastImageDirectory = Path.GetDirectoryName(selectedPath) ?? lastImageDirectory;
				SaveLastImageDirectory();
				return true;
			}
			finally
			{
				Marshal.FreeHGlobal(filePointer);
				Marshal.FreeHGlobal(titlePointer);
				Marshal.FreeHGlobal(filterPointer);
				if (initialDirectoryPointer != IntPtr.Zero)
				{
					Marshal.FreeHGlobal(initialDirectoryPointer);
				}
			}
		}

		private static string LoadLastImageDirectory()
		{
			try
			{
				if (File.Exists(LastImageDirectoryPath))
				{
					string savedDirectory = File.ReadAllText(LastImageDirectoryPath).Trim();
					if (Directory.Exists(savedDirectory))
					{
						return savedDirectory;
					}
				}
			}
			catch
			{
			}

			return Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
		}

		private static void SaveLastImageDirectory()
		{
			try
			{
				Directory.CreateDirectory(AvatarDirectory);
				File.WriteAllText(LastImageDirectoryPath, lastImageDirectory ?? string.Empty);
			}
			catch
			{
			}
		}

		internal static void SaveTexture(Texture2D texture)
		{
			Directory.CreateDirectory(AvatarDirectory);
			using FileStream stream = File.Create(AvatarPath);
			texture.SaveAsPng(stream, texture.Width, texture.Height);
		}

		internal static byte[] CreateNetworkAvatarBytes(Texture2D texture)
		{
			Color[] sourcePixels = new Color[texture.Width * texture.Height];
			texture.GetData(sourcePixels);
			byte[] avatarData = CreateThumbnailPng(texture, sourcePixels, 128);
			return avatarData.Length <= TerraChat.MaximumNetworkAvatarBytes
				? avatarData
				: CreateThumbnailPng(texture, sourcePixels, 96);
		}

		internal static Texture2D CreateDisplayAvatar(Texture2D texture)
		{
			const int displaySize = 128;
			Color[] sourcePixels = new Color[texture.Width * texture.Height];
			texture.GetData(sourcePixels);
			Color[] displayPixels = CreateThumbnailPixels(texture, sourcePixels, displaySize);
			Texture2D displayTexture = new(Main.graphics.GraphicsDevice, displaySize, displaySize);
			displayTexture.SetData(displayPixels);
			return displayTexture;
		}

		private static byte[] CreateThumbnailPng(Texture2D texture, Color[] sourcePixels, int thumbnailSize)
		{
			Color[] thumbnailPixels = CreateThumbnailPixels(texture, sourcePixels, thumbnailSize);

			using Texture2D thumbnail = new(Main.graphics.GraphicsDevice, thumbnailSize, thumbnailSize);
			thumbnail.SetData(thumbnailPixels);
			using MemoryStream stream = new();
			thumbnail.SaveAsPng(stream, thumbnailSize, thumbnailSize);
			return stream.ToArray();
		}

		private static Color[] CreateThumbnailPixels(Texture2D texture, Color[] sourcePixels, int thumbnailSize)
		{
			Color[] thumbnailPixels = new Color[thumbnailSize * thumbnailSize];
			for (int y = 0; y < thumbnailSize; y++)
			{
				float sourceY = (y + 0.5f) * texture.Height / thumbnailSize - 0.5f;
				for (int x = 0; x < thumbnailSize; x++)
				{
					float sourceX = (x + 0.5f) * texture.Width / thumbnailSize - 0.5f;
					thumbnailPixels[y * thumbnailSize + x] = SampleBilinear(sourcePixels, texture.Width, texture.Height, sourceX, sourceY);
				}
			}
			return thumbnailPixels;
		}

		internal static Texture2D CreateCircularAvatar(Texture2D source, float zoom, Vector2 pan)
		{
			Color[] sourcePixels = new Color[source.Width * source.Height];
			source.GetData(sourcePixels);
			return CreateCircularAvatar(source, sourcePixels, zoom, pan);
		}

		internal static Texture2D CreateCircularAvatar(Texture2D source, Color[] sourcePixels, float zoom, Vector2 pan)
		{
			zoom = MathHelper.Clamp(zoom, 1f, 4f);
			pan = ClampPan(source, zoom, pan);
			float scale = GetCoverScale(source, zoom);
			Color[] outputPixels = new Color[AvatarTextureSize * AvatarTextureSize];

			float center = AvatarTextureSize * 0.5f;
			float radius = center - 1f;
			for (int y = 0; y < AvatarTextureSize; y++)
			{
				float offsetY = y + 0.5f - center;
				for (int x = 0; x < AvatarTextureSize; x++)
				{
					float offsetX = x + 0.5f - center;
					float distance = (float)Math.Sqrt(offsetX * offsetX + offsetY * offsetY);
					float coverage = MathHelper.Clamp(radius + 0.75f - distance, 0f, 1f);
					if (coverage <= 0f)
					{
						continue;
					}

					float sourceX = (x + 0.5f - center - pan.X) / scale + source.Width * 0.5f - 0.5f;
					float sourceY = (y + 0.5f - center - pan.Y) / scale + source.Height * 0.5f - 0.5f;
					outputPixels[y * AvatarTextureSize + x] = SampleBilinear(sourcePixels, source.Width, source.Height, sourceX, sourceY) * coverage;
				}
			}

			Texture2D output = new(Main.graphics.GraphicsDevice, AvatarTextureSize, AvatarTextureSize);
			output.SetData(outputPixels);
			return output;
		}

		internal static Vector2 ClampPan(Texture2D source, float zoom, Vector2 pan)
		{
			float scale = GetCoverScale(source, zoom);
			float maximumX = Math.Max(0f, (source.Width * scale - AvatarTextureSize) * 0.5f);
			float maximumY = Math.Max(0f, (source.Height * scale - AvatarTextureSize) * 0.5f);
			return new Vector2(
				MathHelper.Clamp(pan.X, -maximumX, maximumX),
				MathHelper.Clamp(pan.Y, -maximumY, maximumY));
		}

		private static float GetCoverScale(Texture2D source, float zoom)
		{
			return Math.Max(AvatarTextureSize / (float)source.Width, AvatarTextureSize / (float)source.Height) * zoom;
		}

		private static Color SampleBilinear(Color[] pixels, int width, int height, float x, float y)
		{
			x = MathHelper.Clamp(x, 0f, width - 1f);
			y = MathHelper.Clamp(y, 0f, height - 1f);
			int left = (int)Math.Floor(x);
			int top = (int)Math.Floor(y);
			int right = Math.Min(left + 1, width - 1);
			int bottom = Math.Min(top + 1, height - 1);
			float horizontal = x - left;
			float vertical = y - top;
			Color topColor = Color.Lerp(pixels[top * width + left], pixels[top * width + right], horizontal);
			Color bottomColor = Color.Lerp(pixels[bottom * width + left], pixels[bottom * width + right], horizontal);
			return Color.Lerp(topColor, bottomColor, vertical);
		}

		[DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static extern bool GetOpenFileName(ref OpenFileName openFileName);

		[DllImport("user32.dll")]
		private static extern IntPtr GetForegroundWindow();

		[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
		private struct OpenFileName
		{
			internal int StructSize;
			internal IntPtr Owner;
			internal IntPtr Instance;
			internal IntPtr Filter;
			internal IntPtr CustomFilter;
			internal int MaxCustomFilter;
			internal int FilterIndex;
			internal IntPtr File;
			internal int MaxFile;
			internal IntPtr FileTitle;
			internal int MaxFileTitle;
			internal IntPtr InitialDirectory;
			internal IntPtr Title;
			internal int Flags;
			internal short FileOffset;
			internal short FileExtension;
			internal IntPtr DefaultExtension;
			internal IntPtr CustomData;
			internal IntPtr Hook;
			internal IntPtr TemplateName;
			internal IntPtr Reserved;
			internal int ReservedValue;
			internal int FlagsExtended;
		}
	}
}
