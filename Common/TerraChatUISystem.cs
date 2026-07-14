using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using ReLogic.OS;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using Terraria;
using Terraria.Chat;
using Terraria.GameContent;
using Terraria.GameInput;
using Terraria.ModLoader;
using Terraria.UI;
using Terraria.UI.Chat;

namespace TerraChat.Common
{
	[Autoload(Side = ModSide.Client)]
	internal sealed class TerraChatUISystem : ModSystem
	{
		private static TerraChatUISystem instance;
		private static On_Main.orig_DrawInterface_36_Cursor deferredCursorDraw;
		private static bool deferCursorDraw;
		private UserInterface userInterface;
		private TerraChatPanelState panelState;

		internal static void NotifyMessageAdded()
		{
			instance?.panelState?.StickToBottom();
		}

		internal static void RequestLocalProfileSync()
		{
			if (instance?.panelState != null)
			{
				instance.panelState.LocalProfileSyncPending = true;
			}
		}

		internal static void UpdateNetworkAvatar(int playerId, byte[] avatarData)
		{
			instance?.panelState?.SetNetworkAvatar(playerId, avatarData);
		}

		public override void Load()
		{
			instance = this;
			userInterface = new UserInterface();
			panelState = new TerraChatPanelState(this);
			panelState.Activate();
			TextInputEXT.TextInput += HandleTextInput;
			On_Main.DrawInterface += DrawInterfaceAfterVanilla;
			On_Main.DrawInterface_36_Cursor += DeferCursorUntilAfterTerraChat;
		}

		public override void Unload()
		{
			On_Main.DrawInterface_36_Cursor -= DeferCursorUntilAfterTerraChat;
			On_Main.DrawInterface -= DrawInterfaceAfterVanilla;
			TextInputEXT.TextInput -= HandleTextInput;
			panelState?.Dispose();
			panelState = null;
			userInterface = null;
			instance = null;
			deferredCursorDraw = null;
			deferCursorDraw = false;
		}

		public override void UpdateUI(GameTime gameTime)
		{
			if (userInterface?.CurrentState != null)
			{
				userInterface.Update(gameTime);
			}
		}

		public override void PostUpdateEverything()
		{
			if (!Main.gameMenu)
			{
				panelState?.TrySyncLocalProfile();
			}
		}

		public override void PostUpdateInput()
		{
			bool isOpen = userInterface?.CurrentState != null;
			if (TerraChat.ToggleChatKeybind?.JustPressed == true && (!isOpen || !panelState.InputFocused) && (isOpen || !Main.drawingPlayerChat))
			{
				if (isOpen)
				{
					Close();
				}
				else
				{
					Open();
				}
				return;
			}

			if (userInterface?.CurrentState != null)
			{
				panelState.CaptureTextInput();
				Main.chatRelease = false;
			}
		}

		private static void DrawInterfaceAfterVanilla(On_Main.orig_DrawInterface original, Main self, GameTime gameTime)
		{
			UserInterface activeInterface = instance?.userInterface;
			bool drawTerraChat = activeInterface?.CurrentState != null;
			deferredCursorDraw = null;
			deferCursorDraw = drawTerraChat;
			try
			{
				original(self, gameTime);
			}
			finally
			{
				deferCursorDraw = false;
			}

			if (!drawTerraChat)
			{
				deferredCursorDraw = null;
				return;
			}

			bool batchStarted = false;
			PlayerInput.SetZoom_UI();
			try
			{
				Main.spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, null, null, null, null, Main.UIScaleMatrix);
				batchStarted = true;
				activeInterface.Draw(Main.spriteBatch, gameTime);
				deferredCursorDraw?.Invoke();
			}
			finally
			{
				if (batchStarted)
				{
					Main.spriteBatch.End();
				}
				PlayerInput.SetZoom_World();
				deferredCursorDraw = null;
			}
		}

		private static void DeferCursorUntilAfterTerraChat(On_Main.orig_DrawInterface_36_Cursor original)
		{
			if (deferCursorDraw)
			{
				deferredCursorDraw = original;
				return;
			}

			original();
		}

		internal void Close()
		{
			TextInputEXT.StopTextInput();
			panelState?.PrepareForClose();
			userInterface?.SetState(null);
		}

		private void Open()
		{
			panelState.ResetForOpen();
			userInterface.SetState(panelState);
		}

		private void HandleTextInput(char character)
		{
			if (userInterface?.CurrentState != null)
			{
				panelState.QueueTextInput(character);
			}
		}
	}

	internal sealed class TerraChatPanelState : UIState, IDisposable
	{
		private const int MaximumInputLength = 1000;
		private const int MaximumWrappedLines = 4096;
		private const int BackspaceRepeatDelayFrames = 21;
		private const int BackspaceRepeatIntervalFrames = 3;
		private const int NavigationRepeatDelayFrames = 21;
		private const int NavigationRepeatIntervalFrames = 3;
		private const int CaretForceVisibleFrames = 45;
		private const float MouseWheelScrollScale = 0.5f;
		private const float SmoothScrollLerp = 0.42f;
		private const int ScrollBarTrackWidth = 3;
		private const int ScrollBarThumbWidth = 7;
		private const int HistoryBottomPadding = 8;
		private static readonly RasterizerState ScissorRasterizerState = new() { ScissorTestEnable = true };

		private readonly TerraChatUISystem owner;
		private readonly List<char> queuedTextInput = new();
		private readonly Dictionary<int, Texture2D> networkAvatars = new();
		private readonly Dictionary<ChatEntry, CachedMessageLayout> messageLayoutCache = new();
		private readonly List<AvatarHitTarget> visibleAvatarTargets = new();
		private readonly List<MessageHitTarget> visibleMessageTargets = new();
		private readonly List<HistoryBubbleLayout> historySelectionLayouts = new();
		private Rectangle panelRectangle;
		private Rectangle historyRectangle;
		private Rectangle inputRectangle;
		private Rectangle inputScrollTrackRectangle;
		private Rectangle inputScrollThumbRectangle;
		private Rectangle sendButtonRectangle;
		private Rectangle closeButtonRectangle;
		private Rectangle clearHistoryButtonRectangle;
		private Rectangle clearHistoryDialogRectangle;
		private Rectangle clearHistoryConfirmRectangle;
		private Rectangle clearHistoryCancelRectangle;
		private Rectangle headerAvatarRectangle;
		private Rectangle scrollTrackRectangle;
		private Rectangle scrollThumbRectangle;
		private Rectangle cropDialogRectangle;
		private Rectangle cropPreviewRectangle;
		private Rectangle cropMinusRectangle;
		private Rectangle cropPlusRectangle;
		private Rectangle cropCancelRectangle;
		private Rectangle cropConfirmRectangle;
		private Rectangle profileCardRectangle;
		private Rectangle profileAvatarRectangle;
		private Rectangle profileNameRectangle;
		private Rectangle profileDescriptionRectangle;
		private Rectangle profileCancelRectangle;
		private Rectangle profileSaveRectangle;
		private string inputText = string.Empty;
		private string chatInputDraft = string.Empty;
		private string profileNameDraft = string.Empty;
		private string profileDescriptionDraft = string.Empty;
		private TextInputTarget inputTarget = TextInputTarget.Chat;
		private bool profileCardOpen;
		private int profileTargetPlayerId = -1;
		private bool fileDialogOpen;
		private volatile bool fileDialogCompleted;
		private string selectedImagePath;
		private Exception fileDialogException;
		internal bool LocalProfileSyncPending { get; set; }
		private int inputCursorIndex;
		private int selectionAnchorIndex;
		private KeyboardState lastCaptureKeyState;
		private int backspaceHeldFrames;
		private int backspaceRepeatFrames;
		private Keys navigationRepeatKey;
		private int navigationHeldFrames;
		private int navigationRepeatFrames;
		private float verticalNavigationHorizontalPosition = -1f;
		private ulong caretForceVisibleUntilTick;
		private bool clearHistoryConfirmationOpen;
		private bool revealInputCursor;
		private int inputFirstVisibleLine;
		private bool inputScrollDragging;
		private int inputScrollDragStartY;
		private int inputScrollDragStartLine;
		private bool inputMouseSelecting;
		private bool historyMouseSelecting;
		private HistorySelectionPoint historySelectionAnchor = HistorySelectionPoint.Empty;
		private HistorySelectionPoint historySelectionActive = HistorySelectionPoint.Empty;
		private float scrollPixels;
		private float targetScrollPixels;
		private float maximumScrollPixels;
		private bool stickToBottom = true;
		private bool scrollDragging;
		private int scrollDragStartY;
		private float scrollDragStartTarget;
		private Texture2D localAvatar;
		private Texture2D localDisplayAvatar;
		private Texture2D smoothCircleTexture;
		private Texture2D inverseCircleTexture;
		private bool avatarLoadAttempted;
		private Texture2D cropSource;
		private Color[] cropSourcePixels;
		private float cropZoom = 1f;
		private Vector2 cropPan;
		private bool cropDragging;
		private Point cropDragLastMouse;
		internal bool InputFocused { get; private set; }

		internal TerraChatPanelState(TerraChatUISystem owner)
		{
			this.owner = owner;
		}

		internal void ResetForOpen()
		{
			SaveActiveInputDraft();
			inputTarget = TextInputTarget.Chat;
			inputText = chatInputDraft;
			profileCardOpen = false;
			clearHistoryConfirmationOpen = false;
			queuedTextInput.Clear();
			lastCaptureKeyState = Main.keyState;
			stickToBottom = true;
			scrollDragging = false;
			InputFocused = false;
			inputMouseSelecting = false;
			ClearHistorySelection();
			inputScrollDragging = false;
			MoveCursor(inputText.Length, false);
			ResetBackspaceRepeatState();
			ResetNavigationRepeatState();
		}

		internal void StickToBottom()
		{
			stickToBottom = true;
		}

		internal void PrepareForClose()
		{
			SaveActiveInputDraft();
			InputFocused = false;
			profileCardOpen = false;
			clearHistoryConfirmationOpen = false;
			ClearHistorySelection();
			inputScrollDragging = false;
			CloseCropDialog();
		}

		internal void QueueTextInput(char character)
		{
			if (InputFocused)
			{
				queuedTextInput.Add(character);
			}
		}

		internal void CaptureTextInput()
		{
			if (cropSource != null)
			{
				KeyboardState cropKeyState = Main.keyState;
				if (IsNewKeyPress(cropKeyState, Keys.Escape) || Main.inputTextEscape)
				{
					Main.inputTextEscape = false;
					CloseCropDialog();
				}
				queuedTextInput.Clear();
				lastCaptureKeyState = cropKeyState;
				ResetBackspaceRepeatState();
				ResetNavigationRepeatState();
				return;
			}

			if (clearHistoryConfirmationOpen)
			{
				KeyboardState confirmationKeyState = Main.keyState;
				if (IsNewKeyPress(confirmationKeyState, Keys.Escape) || Main.inputTextEscape)
				{
					Main.inputTextEscape = false;
					clearHistoryConfirmationOpen = false;
				}
				queuedTextInput.Clear();
				lastCaptureKeyState = confirmationKeyState;
				ResetBackspaceRepeatState();
				ResetNavigationRepeatState();
				return;
			}

			if (!InputFocused)
			{
				KeyboardState outputKeyState = Main.keyState;
				if (IsControlDown(outputKeyState) && IsNewKeyPress(outputKeyState, Keys.C))
				{
					CopyHistorySelectionToClipboard();
				}
				queuedTextInput.Clear();
				lastCaptureKeyState = outputKeyState;
				ResetBackspaceRepeatState();
				ResetNavigationRepeatState();
				return;
			}

			PlayerInput.WritingText = true;
			Main.instance.HandleIME();
			TextInputEXT.StartTextInput();
			TextInputEXT.SetInputRectangle(GetActiveInputRectangle());

			string oldInputText = inputText;
			KeyboardState keyState = Main.keyState;
			bool enterPressed = IsNewKeyPress(keyState, Keys.Enter);
			bool shiftDown = IsShiftDown(keyState);
			bool commandHandled = HandleCommandShortcuts(keyState);
			HandleNavigationKeys(keyState);

			if (IsControlDown(keyState))
			{
				queuedTextInput.Clear();
			}
			else if (!commandHandled)
			{
				ApplyQueuedTextInput();
			}

			if (ShouldApplyBackspace(keyState))
			{
				DeleteBackward();
			}

			if (IsNewKeyPress(keyState, Keys.Delete))
			{
				DeleteForward();
			}

			int maximumLength = GetMaximumInputLength();
			if (inputText.Length > maximumLength)
			{
				inputText = inputText.Substring(0, maximumLength);
				MoveCursor(inputCursorIndex, HasSelection());
			}

			if (inputText != oldInputText)
			{
				stickToBottom = true;
			}

			if (enterPressed || Main.inputTextEnter)
			{
				ConsumeEnterInput();
				if (inputTarget == TextInputTarget.ProfileName)
				{
					SwitchInputTarget(TextInputTarget.ProfileDescription);
					lastCaptureKeyState = keyState;
					return;
				}

				if (inputTarget == TextInputTarget.ProfileDescription || shiftDown || IsControlDown(keyState))
				{
					InsertTextAtCursor("\n");
					lastCaptureKeyState = keyState;
					return;
				}

				if (inputTarget == TextInputTarget.Chat)
				{
					SubmitMessage();
				}
				lastCaptureKeyState = keyState;
				return;
			}

			if (IsNewKeyPress(keyState, Keys.Escape) || Main.inputTextEscape)
			{
				Main.inputTextEscape = false;
				if (profileCardOpen)
				{
					CloseProfileCard(false);
				}
				else
				{
					owner.Close();
				}
				lastCaptureKeyState = keyState;
				return;
			}

			lastCaptureKeyState = keyState;
		}

		private void ApplyQueuedTextInput()
		{
			if (queuedTextInput.Count == 0)
			{
				return;
			}

			for (int index = 0; index < queuedTextInput.Count && inputText.Length < GetMaximumInputLength(); index++)
			{
				char character = queuedTextInput[index];
				if (character == '\b')
				{
					continue;
				}

				if (character == '\t')
				{
					InsertTextAtCursor("    ");
					continue;
				}

				if (character == '\r' || character == '\n' || char.IsControl(character))
				{
					continue;
				}

				InsertTextAtCursor(character.ToString());
			}

			queuedTextInput.Clear();
		}

		private bool HandleCommandShortcuts(KeyboardState keyState)
		{
			if (!IsControlDown(keyState))
			{
				return false;
			}

			if (IsNewKeyPress(keyState, Keys.A))
			{
				SelectAllText();
				return true;
			}

			if (IsNewKeyPress(keyState, Keys.C))
			{
				CopySelectionToClipboard();
				return true;
			}

			if (IsNewKeyPress(keyState, Keys.X))
			{
				CutSelectionToClipboard();
				return true;
			}

			if (IsNewKeyPress(keyState, Keys.V))
			{
				PasteClipboardText();
				return true;
			}

			return false;
		}

		private void HandleNavigationKeys(KeyboardState keyState)
		{
			bool shiftDown = IsShiftDown(keyState);
			bool controlDown = IsControlDown(keyState);
			bool moved = false;
			if (ShouldApplyNavigationKey(keyState, Keys.Left))
			{
				MoveCursor(controlDown ? FindPreviousWordBoundary(inputCursorIndex) : GetPreviousTextElementStart(inputCursorIndex), shiftDown);
				moved = true;
			}
			else if (ShouldApplyNavigationKey(keyState, Keys.Right))
			{
				MoveCursor(controlDown ? FindNextWordBoundary(inputCursorIndex) : GetNextTextElementStart(inputCursorIndex), shiftDown);
				moved = true;
			}
			else if (ShouldApplyNavigationKey(keyState, Keys.Up))
			{
				MoveCursorVertically(-1, shiftDown);
				moved = true;
			}
			else if (ShouldApplyNavigationKey(keyState, Keys.Down))
			{
				MoveCursorVertically(1, shiftDown);
				moved = true;
			}

			if (!moved
				&& !keyState.IsKeyDown(Keys.Left)
				&& !keyState.IsKeyDown(Keys.Right)
				&& !keyState.IsKeyDown(Keys.Up)
				&& !keyState.IsKeyDown(Keys.Down))
			{
				ResetNavigationRepeatState();
			}

			if (IsNewKeyPress(keyState, Keys.Home))
			{
				ResetNavigationRepeatState();
				MoveCursor(0, shiftDown);
			}

			if (IsNewKeyPress(keyState, Keys.End))
			{
				ResetNavigationRepeatState();
				MoveCursor(inputText.Length, shiftDown);
			}
		}

		private void MoveCursorVertically(int direction, bool selecting)
		{
			Rectangle textArea = GetTextArea();
			float scale = GetActiveInputTextScale();
			List<EditableTextLine> lines = WrapEditableText(inputText ?? string.Empty, textArea, scale);
			int currentLineIndex = FindEditableLineIndex(lines, inputCursorIndex);
			int targetLineIndex = Math.Min(Math.Max(currentLineIndex + direction, 0), lines.Count - 1);
			if (targetLineIndex == currentLineIndex)
			{
				return;
			}

			float horizontalPosition = verticalNavigationHorizontalPosition >= 0f
				? verticalNavigationHorizontalPosition
				: MeasureEditablePrefix(lines[currentLineIndex], inputCursorIndex);
			EditableTextLine targetLine = lines[targetLineIndex];
			MoveCursor(FindEditableIndexAtPosition(targetLine, horizontalPosition), selecting);
			verticalNavigationHorizontalPosition = horizontalPosition;
		}

		private static bool IsControlDown(KeyboardState keyState)
		{
			return keyState.IsKeyDown(Keys.LeftControl) || keyState.IsKeyDown(Keys.RightControl);
		}

		private static bool IsShiftDown(KeyboardState keyState)
		{
			return keyState.IsKeyDown(Keys.LeftShift) || keyState.IsKeyDown(Keys.RightShift);
		}

		private bool ShouldApplyBackspace(KeyboardState keyState)
		{
			if (!keyState.IsKeyDown(Keys.Back))
			{
				ResetBackspaceRepeatState();
				return false;
			}

			if (!lastCaptureKeyState.IsKeyDown(Keys.Back))
			{
				backspaceHeldFrames = 1;
				backspaceRepeatFrames = 0;
				return true;
			}

			backspaceHeldFrames++;
			if (backspaceHeldFrames < BackspaceRepeatDelayFrames)
			{
				return false;
			}

			backspaceRepeatFrames++;
			if (backspaceRepeatFrames < BackspaceRepeatIntervalFrames)
			{
				return false;
			}

			backspaceRepeatFrames = 0;
			return true;
		}

		private void ResetBackspaceRepeatState()
		{
			backspaceHeldFrames = 0;
			backspaceRepeatFrames = 0;
		}

		private bool ShouldApplyNavigationKey(KeyboardState keyState, Keys key)
		{
			if (!keyState.IsKeyDown(key))
			{
				if (navigationRepeatKey == key)
				{
					ResetNavigationRepeatState();
				}
				return false;
			}

			if (navigationRepeatKey != key || !lastCaptureKeyState.IsKeyDown(key))
			{
				navigationRepeatKey = key;
				navigationHeldFrames = 1;
				navigationRepeatFrames = 0;
				return true;
			}

			navigationHeldFrames++;
			if (navigationHeldFrames < NavigationRepeatDelayFrames)
			{
				return false;
			}

			navigationRepeatFrames++;
			if (navigationRepeatFrames < NavigationRepeatIntervalFrames)
			{
				return false;
			}

			navigationRepeatFrames = 0;
			return true;
		}

		private void ResetNavigationRepeatState()
		{
			navigationRepeatKey = Keys.None;
			navigationHeldFrames = 0;
			navigationRepeatFrames = 0;
			verticalNavigationHorizontalPosition = -1f;
		}

		private void InsertTextAtCursor(string text)
		{
			text = SanitizeInputText(text);
			if (string.IsNullOrEmpty(text))
			{
				return;
			}

			DeleteSelection();
			int available = GetMaximumInputLength() - inputText.Length;
			if (available <= 0)
			{
				return;
			}

			if (text.Length > available)
			{
				text = text.Substring(0, available);
			}

			inputCursorIndex = ClampTextIndex(inputCursorIndex);
			inputText = inputText.Insert(inputCursorIndex, text);
			MoveCursor(inputCursorIndex + text.Length, false);
		}

		private void DeleteBackward()
		{
			if (DeleteSelection() || inputCursorIndex <= 0)
			{
				return;
			}

			int removeStart = GetPreviousTextElementStart(inputCursorIndex);
			inputText = inputText.Remove(removeStart, inputCursorIndex - removeStart);
			MoveCursor(removeStart, false);
		}

		private void DeleteForward()
		{
			if (DeleteSelection() || inputCursorIndex >= inputText.Length)
			{
				return;
			}

			int removeEnd = GetNextTextElementStart(inputCursorIndex);
			inputText = inputText.Remove(inputCursorIndex, removeEnd - inputCursorIndex);
			MoveCursor(inputCursorIndex, false);
		}

		private bool DeleteSelection()
		{
			if (!HasSelection())
			{
				return false;
			}

			int start = GetSelectionStart();
			inputText = inputText.Remove(start, GetSelectionEnd() - start);
			MoveCursor(start, false);
			return true;
		}

		private void MoveCursor(int index, bool selecting)
		{
			verticalNavigationHorizontalPosition = -1f;
			inputCursorIndex = ClampTextIndex(index);
			if (!selecting)
			{
				selectionAnchorIndex = inputCursorIndex;
			}
			caretForceVisibleUntilTick = Main.GameUpdateCount + CaretForceVisibleFrames;
			revealInputCursor = true;
		}

		private void SelectAllText()
		{
			verticalNavigationHorizontalPosition = -1f;
			selectionAnchorIndex = 0;
			inputCursorIndex = inputText.Length;
			caretForceVisibleUntilTick = Main.GameUpdateCount + CaretForceVisibleFrames;
			revealInputCursor = true;
		}

		private bool ShouldDrawCaret()
		{
			return InputFocused && (Main.GameUpdateCount <= caretForceVisibleUntilTick || (int)(Main.GlobalTimeWrappedHourly * 2f) % 2 == 0);
		}

		private bool HasSelection()
		{
			return inputCursorIndex != selectionAnchorIndex;
		}

		private int GetSelectionStart()
		{
			return Math.Min(ClampTextIndex(inputCursorIndex), ClampTextIndex(selectionAnchorIndex));
		}

		private int GetSelectionEnd()
		{
			return Math.Max(ClampTextIndex(inputCursorIndex), ClampTextIndex(selectionAnchorIndex));
		}

		private string GetSelectedText()
		{
			return HasSelection() ? inputText.Substring(GetSelectionStart(), GetSelectionEnd() - GetSelectionStart()) : string.Empty;
		}

		private void CopySelectionToClipboard()
		{
			string selectedText = GetSelectedText();
			if (!string.IsNullOrEmpty(selectedText))
			{
				SetClipboardText(selectedText);
			}
		}

		private void CopyHistorySelectionToClipboard()
		{
			if (!TryGetOrderedHistorySelection(out HistorySelectionPoint start, out HistorySelectionPoint end))
			{
				return;
			}

			List<ChatEntry> messages = Main.LocalPlayer.GetModPlayer<TerraChatPlayer>().History;
			if (start.MessageIndex < 0 || end.MessageIndex >= messages.Count)
			{
				ClearHistorySelection();
				return;
			}

			StringBuilder selectedText = new();
			for (int messageIndex = start.MessageIndex; messageIndex <= end.MessageIndex; messageIndex++)
			{
				string text = messages[messageIndex].Text ?? string.Empty;
				int selectionStart = messageIndex == start.MessageIndex ? Math.Min(start.CharacterIndex, text.Length) : 0;
				int selectionEnd = messageIndex == end.MessageIndex ? Math.Min(end.CharacterIndex, text.Length) : text.Length;
				if (selectionEnd > selectionStart)
				{
					selectedText.Append(text, selectionStart, selectionEnd - selectionStart);
				}
				if (messageIndex < end.MessageIndex)
				{
					selectedText.AppendLine();
				}
			}

			if (selectedText.Length > 0)
			{
				SetClipboardText(selectedText.ToString());
			}
		}

		private bool TryGetHistorySelectionRange(int messageIndex, int textLength, out int start, out int end)
		{
			if (!TryGetOrderedHistorySelection(out HistorySelectionPoint selectionStart, out HistorySelectionPoint selectionEnd)
				|| messageIndex < selectionStart.MessageIndex
				|| messageIndex > selectionEnd.MessageIndex)
			{
				start = 0;
				end = 0;
				return false;
			}

			start = messageIndex == selectionStart.MessageIndex ? Math.Min(selectionStart.CharacterIndex, textLength) : 0;
			end = messageIndex == selectionEnd.MessageIndex ? Math.Min(selectionEnd.CharacterIndex, textLength) : textLength;
			return end > start;
		}

		private bool TryGetOrderedHistorySelection(out HistorySelectionPoint start, out HistorySelectionPoint end)
		{
			if (historySelectionAnchor.MessageIndex < 0 || historySelectionActive.MessageIndex < 0 || historySelectionAnchor.Equals(historySelectionActive))
			{
				start = HistorySelectionPoint.Empty;
				end = HistorySelectionPoint.Empty;
				return false;
			}

			if (historySelectionAnchor.CompareTo(historySelectionActive) <= 0)
			{
				start = historySelectionAnchor;
				end = historySelectionActive;
			}
			else
			{
				start = historySelectionActive;
				end = historySelectionAnchor;
			}
			return true;
		}

		private void ClearHistorySelection()
		{
			historyMouseSelecting = false;
			historySelectionAnchor = HistorySelectionPoint.Empty;
			historySelectionActive = HistorySelectionPoint.Empty;
		}

		private void CutSelectionToClipboard()
		{
			string selectedText = GetSelectedText();
			if (!string.IsNullOrEmpty(selectedText))
			{
				SetClipboardText(selectedText);
				DeleteSelection();
			}
		}

		private void PasteClipboardText()
		{
			string clipboardText = GetClipboardText();
			if (!string.IsNullOrEmpty(clipboardText))
			{
				InsertTextAtCursor(clipboardText);
			}
		}

		private static string GetClipboardText()
		{
			try
			{
				return Platform.Has<IClipboard>() ? Platform.Get<IClipboard>().MultiLineValue ?? string.Empty : string.Empty;
			}
			catch
			{
				return string.Empty;
			}
		}

		private static void SetClipboardText(string text)
		{
			try
			{
				if (Platform.Has<IClipboard>())
				{
					Platform.Get<IClipboard>().Value = text ?? string.Empty;
				}
			}
			catch
			{
			}
		}

		private static string SanitizeInputText(string text)
		{
			if (string.IsNullOrEmpty(text))
			{
				return string.Empty;
			}

			text = text.Replace("\r\n", "\n").Replace('\r', '\n');
			StringBuilder builder = new(text.Length);
			foreach (char character in text)
			{
				if (character == '\n')
				{
					builder.Append('\n');
				}
				else if (character == '\t')
				{
					builder.Append("    ");
				}
				else if (!char.IsControl(character))
				{
					builder.Append(character);
				}
			}
			return builder.ToString();
		}

		private int ClampTextIndex(int index)
		{
			return Math.Min(Math.Max(index, 0), inputText.Length);
		}

		private int GetPreviousTextElementStart(int index)
		{
			index = ClampTextIndex(index);
			if (index <= 0)
			{
				return 0;
			}

			int previous = 0;
			foreach (int elementIndex in StringInfo.ParseCombiningCharacters(inputText))
			{
				if (elementIndex >= index)
				{
					break;
				}
				previous = elementIndex;
			}
			return previous;
		}

		private int GetNextTextElementStart(int index)
		{
			index = ClampTextIndex(index);
			if (index >= inputText.Length)
			{
				return inputText.Length;
			}

			foreach (int elementIndex in StringInfo.ParseCombiningCharacters(inputText))
			{
				if (elementIndex > index)
				{
					return elementIndex;
				}
			}
			return inputText.Length;
		}

		private int FindPreviousWordBoundary(int index)
		{
			index = ClampTextIndex(index);
			while (index > 0 && char.IsWhiteSpace(inputText[index - 1])) index--;
			while (index > 0 && !char.IsWhiteSpace(inputText[index - 1])) index--;
			return index;
		}

		private int FindNextWordBoundary(int index)
		{
			index = ClampTextIndex(index);
			while (index < inputText.Length && char.IsWhiteSpace(inputText[index])) index++;
			while (index < inputText.Length && !char.IsWhiteSpace(inputText[index])) index++;
			return index;
		}

		private static void ConsumeEnterInput()
		{
			Main.inputTextEnter = false;
			Main.chatRelease = false;
			Main.npcChatRelease = false;
			Main.clrInput();
		}

		public override void Update(GameTime gameTime)
		{
			base.Update(gameTime);
			UpdateLayout();
			ProcessFileDialogCompletion();
			Main.LocalPlayer.mouseInterface = true;
			Main.blockMouse = true;
			PlayerInput.LockVanillaMouseScroll("TerraChat.ChatUI");

			Point mouse = new(Main.mouseX, Main.mouseY);
			if (cropSource != null)
			{
				return;
			}
			if (clearHistoryConfirmationOpen)
			{
				return;
			}

			UpdateInputScrollBarLayout();
			HandleInputScroll(mouse);
			HandleInputScrollBar(mouse);
			HandleScroll(mouse);
			HandleScrollBar(mouse);
			HandleHistoryMouseSelection(mouse);
			HandleInputMouseSelection(mouse);
			UpdateSmoothScroll();
		}

		protected override void DrawSelf(SpriteBatch spriteBatch)
		{
			base.DrawSelf(spriteBatch);
			UpdateLayout();
			EnsureAvatarLoaded();

			Texture2D pixel = TextureAssets.MagicPixel.Value;
			spriteBatch.Draw(pixel, new Rectangle(0, 0, Main.screenWidth, Main.screenHeight), Color.Black * 0.28f);
			DrawBox(spriteBatch, panelRectangle, new Color(6, 8, 13) * 0.82f, new Color(126, 146, 178) * 0.76f);
			DrawHeader(spriteBatch);
			DrawClearHistoryButton(spriteBatch);
			DrawCloseButton(spriteBatch);
			DrawBox(spriteBatch, historyRectangle, new Color(3, 6, 12) * 0.66f, new Color(92, 110, 142) * 0.68f);
			DrawHistoryMessages(spriteBatch, GetHistoryArea());
			DrawScrollBar(spriteBatch);
			DrawInput(spriteBatch);
			DrawButton(spriteBatch, sendButtonRectangle, "发送");
			if (profileCardOpen)
			{
				DrawProfileCard(spriteBatch);
			}
			if (cropSource != null)
			{
				DrawCropDialog(spriteBatch);
			}
			if (clearHistoryConfirmationOpen)
			{
				DrawClearHistoryConfirmation(spriteBatch);
			}

			HandleMouseInput();
			DrawImePanel(spriteBatch);
		}

		public void Dispose()
		{
			localAvatar?.Dispose();
			localAvatar = null;
			localDisplayAvatar?.Dispose();
			localDisplayAvatar = null;
			smoothCircleTexture?.Dispose();
			smoothCircleTexture = null;
			inverseCircleTexture?.Dispose();
			inverseCircleTexture = null;
			cropSource?.Dispose();
			cropSource = null;
			cropSourcePixels = null;
			foreach (Texture2D avatar in networkAvatars.Values)
			{
				avatar.Dispose();
			}
			networkAvatars.Clear();
			messageLayoutCache.Clear();
			visibleMessageTargets.Clear();
		}

		private void UpdateLayout()
		{
			int targetWidth = Math.Min(Math.Max(907, (int)(Main.screenWidth * 0.68f)), 1180);
			int targetHeight = Math.Min(Math.Max(627, (int)(Main.screenHeight * 0.76f)), 780);
			int panelWidth = Math.Min(targetWidth, Main.screenWidth - 64);
			int panelHeight = Math.Min(targetHeight, Main.screenHeight - 64);
			panelRectangle = new Rectangle(
				(Main.screenWidth - panelWidth) / 2,
				(Main.screenHeight - panelHeight) / 2,
				panelWidth,
				panelHeight);

			int buttonWidth = panelWidth < 760 ? 96 : 112;
			int buttonHeight = 34;
			int buttonY = panelRectangle.Bottom - 44;
			sendButtonRectangle = new Rectangle(panelRectangle.Right - 18 - buttonWidth, buttonY, buttonWidth, buttonHeight);
			closeButtonRectangle = new Rectangle(panelRectangle.Right - 42, panelRectangle.Y + 14, 24, 24);
			clearHistoryButtonRectangle = new Rectangle(closeButtonRectangle.X - 108, panelRectangle.Y + 10, 96, 32);
			int clearDialogWidth = Math.Min(420, Main.screenWidth - 40);
			int clearDialogHeight = Math.Min(190, Main.screenHeight - 40);
			clearHistoryDialogRectangle = new Rectangle((Main.screenWidth - clearDialogWidth) / 2, (Main.screenHeight - clearDialogHeight) / 2, clearDialogWidth, clearDialogHeight);
			clearHistoryConfirmRectangle = new Rectangle(clearHistoryDialogRectangle.Center.X - 130, clearHistoryDialogRectangle.Bottom - 54, 120, 36);
			clearHistoryCancelRectangle = new Rectangle(clearHistoryDialogRectangle.Center.X + 10, clearHistoryDialogRectangle.Bottom - 54, 120, 36);
			headerAvatarRectangle = new Rectangle(panelRectangle.X + 18, panelRectangle.Y + 5, 40, 40);

			int padding = 14;
			int historyY = panelRectangle.Y + 50;
			int inputHeight = Math.Min(96, Math.Max(68, panelHeight / 7));
			int inputY = buttonY - inputHeight - 10;
			historyRectangle = new Rectangle(panelRectangle.X + padding, historyY, panelRectangle.Width - padding * 2, inputY - historyY - 10);
			inputRectangle = new Rectangle(panelRectangle.X + padding, inputY, panelRectangle.Width - padding * 2, inputHeight);

			int cropWidth = Math.Min(480, Main.screenWidth - 40);
			int cropHeight = Math.Min(540, Main.screenHeight - 40);
			cropDialogRectangle = new Rectangle((Main.screenWidth - cropWidth) / 2, (Main.screenHeight - cropHeight) / 2, cropWidth, cropHeight);
			int previewSize = Math.Min(300, cropHeight - 220);
			cropPreviewRectangle = new Rectangle(cropDialogRectangle.Center.X - previewSize / 2, cropDialogRectangle.Y + 50, previewSize, previewSize);
			cropMinusRectangle = new Rectangle(cropDialogRectangle.X + 46, cropPreviewRectangle.Bottom + 18, 36, 32);
			cropPlusRectangle = new Rectangle(cropDialogRectangle.Right - 82, cropPreviewRectangle.Bottom + 18, 36, 32);
			cropCancelRectangle = new Rectangle(cropDialogRectangle.Center.X - 116, cropDialogRectangle.Bottom - 48, 104, 34);
			cropConfirmRectangle = new Rectangle(cropDialogRectangle.Center.X + 12, cropDialogRectangle.Bottom - 48, 104, 34);

			int profileWidth = Math.Min(500, Main.screenWidth - 48);
			int profileHeight = Math.Min(500, Main.screenHeight - 48);
			profileCardRectangle = new Rectangle((Main.screenWidth - profileWidth) / 2, (Main.screenHeight - profileHeight) / 2, profileWidth, profileHeight);
			profileAvatarRectangle = new Rectangle(profileCardRectangle.Center.X - 48, profileCardRectangle.Y + 48, 96, 96);
			profileNameRectangle = new Rectangle(profileCardRectangle.X + 34, profileAvatarRectangle.Bottom + 42, profileCardRectangle.Width - 68, 48);
			profileDescriptionRectangle = new Rectangle(profileCardRectangle.X + 34, profileNameRectangle.Bottom + 42, profileCardRectangle.Width - 68, 112);
			profileCancelRectangle = new Rectangle(profileCardRectangle.Center.X - 112, profileCardRectangle.Bottom - 46, 100, 32);
			profileSaveRectangle = new Rectangle(profileCardRectangle.Center.X + 12, profileCardRectangle.Bottom - 46, 100, 32);
		}

		private void DrawHeader(SpriteBatch spriteBatch)
		{
			bool hovered = headerAvatarRectangle.Contains(new Point(Main.mouseX, Main.mouseY));
			Color fill = hovered ? new Color(16, 24, 34) : new Color(8, 11, 18) * 0.84f;
			DrawAvatarBadge(spriteBatch, headerAvatarRectangle, Main.LocalPlayer.name, localDisplayAvatar ?? localAvatar, true, fill);
			Utils.DrawBorderString(spriteBatch, "聊天记录", new Vector2(headerAvatarRectangle.Right + 10, panelRectangle.Y + 16), Color.White, 1f);
		}

		private void DrawCloseButton(SpriteBatch spriteBatch)
		{
			bool hovered = closeButtonRectangle.Contains(new Point(Main.mouseX, Main.mouseY));
			DrawBox(
				spriteBatch,
				closeButtonRectangle,
				hovered ? new Color(76, 28, 38) * 0.9f : new Color(22, 16, 22) * 0.72f,
				hovered ? new Color(228, 144, 146) : new Color(130, 96, 108) * 0.78f);
			Utils.DrawBorderString(spriteBatch, "X", new Vector2(closeButtonRectangle.X + 6, closeButtonRectangle.Y + 2), Color.White, 0.9f);
		}

		private void DrawClearHistoryButton(SpriteBatch spriteBatch)
		{
			DrawButton(spriteBatch, clearHistoryButtonRectangle, "清空记录");
		}

		private void DrawClearHistoryConfirmation(SpriteBatch spriteBatch)
		{
			spriteBatch.Draw(TextureAssets.MagicPixel.Value, new Rectangle(0, 0, Main.screenWidth, Main.screenHeight), Color.Black * 0.58f);
			DrawBox(spriteBatch, clearHistoryDialogRectangle, new Color(6, 8, 13) * 0.99f, new Color(152, 164, 184) * 0.92f);
			Utils.DrawBorderString(spriteBatch, "清空聊天记录", new Vector2(clearHistoryDialogRectangle.X + 22, clearHistoryDialogRectangle.Y + 20), Color.White, 1f);

			const string warning = "确定要删除全部聊天记录吗？此操作无法撤销。";
			Vector2 warningSize = FontAssets.MouseText.Value.MeasureString(warning);
			float warningScale = Math.Min(0.82f, (clearHistoryDialogRectangle.Width - 44f) / Math.Max(1f, warningSize.X));
			ChatManager.DrawColorCodedStringWithShadow(
				spriteBatch,
				FontAssets.MouseText.Value,
				warning,
				new Vector2(clearHistoryDialogRectangle.Center.X, clearHistoryDialogRectangle.Y + 82),
				new Color(210, 214, 222),
				0f,
				new Vector2(warningSize.X * 0.5f, 0f),
				new Vector2(warningScale));

			DrawDangerButton(spriteBatch, clearHistoryConfirmRectangle, "确认");
			DrawButton(spriteBatch, clearHistoryCancelRectangle, "取消");
		}

		private static void DrawDangerButton(SpriteBatch spriteBatch, Rectangle rectangle, string text)
		{
			bool hovered = rectangle.Contains(new Point(Main.mouseX, Main.mouseY));
			DrawBox(
				spriteBatch,
				rectangle,
				hovered ? new Color(102, 28, 38) * 0.96f : new Color(64, 22, 30) * 0.9f,
				hovered ? new Color(246, 118, 126) : new Color(206, 82, 94));
			Vector2 size = FontAssets.MouseText.Value.MeasureString(text);
			ChatManager.DrawColorCodedStringWithShadow(spriteBatch, FontAssets.MouseText.Value, text, rectangle.Center.ToVector2(), Color.White, 0f, size * 0.5f, new Vector2(0.86f));
		}

		private void DrawInput(SpriteBatch spriteBatch)
		{
			Color rim = InputFocused ? new Color(112, 138, 158) * 0.58f : new Color(82, 98, 122) * 0.48f;
			DrawInsetInputBox(spriteBatch, inputRectangle, new Color(3, 6, 12) * 0.7f, rim, InputFocused);
			Rectangle textArea = GetTextArea();
			if (string.IsNullOrEmpty(inputText))
			{
				Utils.DrawBorderString(spriteBatch, "输入消息，按 Enter 发送", textArea.Location.ToVector2(), Color.Gray, 0.9f);
			}

			DrawEditableText(spriteBatch, inputText, textArea, Color.White, 0.9f);
			DrawInputScrollBar(spriteBatch, TextInputTarget.Chat);
		}

		private void DrawProfileCard(SpriteBatch spriteBatch)
		{
			bool isOwnProfile = profileTargetPlayerId == Main.myPlayer;
			TerraChatPlayer targetProfile = GetProfileTarget();
			spriteBatch.Draw(TextureAssets.MagicPixel.Value, new Rectangle(0, 0, Main.screenWidth, Main.screenHeight), Color.Black * 0.46f);
			DrawBox(spriteBatch, profileCardRectangle, new Color(6, 8, 13) * 0.98f, new Color(126, 146, 178) * 0.9f);
			Utils.DrawBorderString(spriteBatch, isOwnProfile ? "个人资料" : "玩家资料", new Vector2(profileCardRectangle.X + 18, profileCardRectangle.Y + 15), Color.White, 1f);
			if (!isOwnProfile)
			{
				Utils.DrawBorderString(spriteBatch, "只读", new Vector2(profileCardRectangle.Right - 62, profileCardRectangle.Y + 19), new Color(132, 142, 158), 0.68f);
			}

			string profileName = GetProfileFieldText(TextInputTarget.ProfileName);
			bool avatarHovered = isOwnProfile && profileAvatarRectangle.Contains(new Point(Main.mouseX, Main.mouseY));
			DrawAvatarBadge(
				spriteBatch,
				profileAvatarRectangle,
				string.IsNullOrWhiteSpace(profileName) ? targetProfile.Player.name : profileName,
				GetPlayerAvatar(profileTargetPlayerId),
				isOwnProfile,
				avatarHovered ? new Color(16, 24, 34) : new Color(8, 11, 18) * 0.9f);
			if (isOwnProfile && (avatarHovered || fileDialogOpen))
			{
				DrawCircle(spriteBatch, profileAvatarRectangle, Color.Black * 0.58f);
				string actionText = fileDialogOpen ? "正在打开…" : "更换头像";
				Vector2 actionSize = FontAssets.MouseText.Value.MeasureString(actionText);
				float actionScale = Math.Min(0.72f, 78f / Math.Max(1f, actionSize.X));
				ChatManager.DrawColorCodedStringWithShadow(
					spriteBatch,
					FontAssets.MouseText.Value,
					actionText,
					profileAvatarRectangle.Center.ToVector2(),
					Color.White,
					0f,
					actionSize * 0.5f,
					new Vector2(actionScale));
			}

			string profileNameLabel = $"资料名（{targetProfile.Player.name}）";
			float profileNameLabelScale = Math.Min(0.82f, profileNameRectangle.Width / Math.Max(1f, FontAssets.MouseText.Value.MeasureString(profileNameLabel).X));
			Utils.DrawBorderString(spriteBatch, profileNameLabel, new Vector2(profileNameRectangle.X, profileNameRectangle.Y - 25), new Color(190, 202, 218), profileNameLabelScale);
			DrawProfileInputField(spriteBatch, profileNameRectangle, TextInputTarget.ProfileName, isOwnProfile ? "输入资料名" : "未设置资料名");
			Utils.DrawBorderString(spriteBatch, "详细描述", new Vector2(profileDescriptionRectangle.X, profileDescriptionRectangle.Y - 25), new Color(190, 202, 218), 0.82f);
			DrawProfileInputField(spriteBatch, profileDescriptionRectangle, TextInputTarget.ProfileDescription, isOwnProfile ? "介绍一下自己" : "暂无描述");

			string description = GetProfileFieldText(TextInputTarget.ProfileDescription);
			string counter = description.Length + "/" + TerraChatPlayer.MaximumDescriptionLength;
			Vector2 counterSize = FontAssets.MouseText.Value.MeasureString(counter) * 0.72f;
			Utils.DrawBorderString(spriteBatch, counter, new Vector2(profileDescriptionRectangle.Right - counterSize.X - 8, profileDescriptionRectangle.Bottom + 5), new Color(150, 164, 184), 0.72f);
			if (isOwnProfile)
			{
				DrawButton(spriteBatch, profileCancelRectangle, "取消");
				DrawButton(spriteBatch, profileSaveRectangle, "保存");
			}
			else
			{
				DrawButton(spriteBatch, GetReadOnlyCloseRectangle(), "关闭");
			}
		}

		private void DrawProfileInputField(SpriteBatch spriteBatch, Rectangle rectangle, TextInputTarget target, string placeholder)
		{
			bool active = InputFocused && inputTarget == target;
			Color rim = active ? new Color(112, 138, 158) * 0.72f : new Color(82, 98, 122) * 0.58f;
			DrawInsetInputBox(spriteBatch, rectangle, new Color(3, 6, 12) * 0.82f, rim, active);
			Rectangle area = GetTextArea(rectangle);
			string text = GetProfileFieldText(target);
			if (string.IsNullOrEmpty(text))
			{
				Utils.DrawBorderString(spriteBatch, placeholder, area.Location.ToVector2(), Color.Gray, 0.82f);
				return;
			}

			if (active)
			{
				DrawEditableText(spriteBatch, inputText, area, Color.White, 0.86f);
				DrawInputScrollBar(spriteBatch, target);
				return;
			}

			int lineCount;
			string[] lines = Utils.WordwrapString(text, FontAssets.MouseText.Value, (int)(area.Width / 0.86f), 8, out lineCount);
			float y = area.Y;
			for (int index = 0; index < lineCount && index < lines.Length && y < area.Bottom; index++)
			{
				Utils.DrawBorderString(spriteBatch, lines[index] ?? string.Empty, new Vector2(area.X, y), Color.White, 0.86f);
				y += FontAssets.MouseText.Value.LineSpacing * 0.86f;
			}
		}

		private void DrawHistoryMessages(SpriteBatch spriteBatch, Rectangle area)
		{
			visibleAvatarTargets.Clear();
			visibleMessageTargets.Clear();
			historySelectionLayouts.Clear();
			TerraChatPlayer chatPlayer = Main.LocalPlayer.GetModPlayer<TerraChatPlayer>();
			List<ChatEntry> messages = chatPlayer.History;
			RemoveStaleMessageLayouts(messages);
			if (historySelectionAnchor.MessageIndex >= messages.Count || historySelectionActive.MessageIndex >= messages.Count)
			{
				ClearHistorySelection();
			}
			if (messages.Count == 0)
			{
				maximumScrollPixels = 0f;
				targetScrollPixels = 0f;
				scrollPixels = 0f;
				DrawEmptyChatState(spriteBatch, area);
				return;
			}

			const float scale = 0.85f;
			const int iconSize = 48;
			const int iconGap = 8;
			const int bubblePadding = 10;
			const int messageGap = 12;
			int maximumBubbleWidth = Math.Max(150, (int)(area.Width * 0.62f));
			List<HistoryBubbleLayout> layouts = new(messages.Count);
			float y = area.Y;
			for (int messageIndex = 0; messageIndex < messages.Count; messageIndex++)
			{
				ChatEntry message = messages[messageIndex];
				int playerId = ResolvePlayerId(message);
				string displayName = GetPlayerDisplayName(playerId, message.Sender);
				HistoryBubbleLayout layout = CreateHistoryBubbleLayout(messageIndex, message, playerId, displayName, area, y, scale, iconSize, iconGap, bubblePadding, maximumBubbleWidth);
				layouts.Add(layout);
				y += layout.Bounds.Height + messageGap;
			}

			float totalHeight = Math.Max(0f, y - area.Y - messageGap + HistoryBottomPadding);
			maximumScrollPixels = Math.Max(0f, totalHeight - area.Height);
			if (stickToBottom)
			{
				scrollPixels = maximumScrollPixels;
				targetScrollPixels = maximumScrollPixels;
				stickToBottom = false;
			}

			scrollPixels = MathHelper.Clamp(scrollPixels, 0f, maximumScrollPixels);
			targetScrollPixels = MathHelper.Clamp(targetScrollPixels, 0f, maximumScrollPixels);

			GraphicsDevice graphicsDevice = spriteBatch.GraphicsDevice;
			Rectangle oldScissorRectangle = graphicsDevice.ScissorRectangle;
			spriteBatch.End();
			graphicsDevice.ScissorRectangle = GetScaledScissorRectangle(area);
			spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, null, null, ScissorRasterizerState, null, Main.UIScaleMatrix);

			foreach (HistoryBubbleLayout originalLayout in layouts)
			{
				HistoryBubbleLayout layout = originalLayout.Offset(scrollPixels);
				historySelectionLayouts.Add(layout);
				if (layout.Bounds.Bottom >= area.Y && layout.Bounds.Y <= area.Bottom)
				{
					if (layout.PlayerId >= 0)
					{
						visibleAvatarTargets.Add(new AvatarHitTarget(layout.IconRectangle, layout.PlayerId));
					}
					MessageHitTarget messageTarget = CreateMessageHitTarget(layout);
					visibleMessageTargets.Add(messageTarget);
					DrawHistoryBubble(spriteBatch, layout, scale, bubblePadding);
					DrawMessageDeleteControl(spriteBatch, messageTarget);
				}
			}

			spriteBatch.End();
			graphicsDevice.ScissorRectangle = oldScissorRectangle;
			spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, null, null, null, null, Main.UIScaleMatrix);
		}

		private void DrawEmptyChatState(SpriteBatch spriteBatch, Rectangle area)
		{
			int avatarSize = 53;
			Rectangle avatarRectangle = new(area.Center.X - avatarSize / 2, area.Center.Y - 54, avatarSize, avatarSize);
			DrawAvatarBadge(spriteBatch, avatarRectangle, Main.LocalPlayer.name, localDisplayAvatar ?? localAvatar, true, new Color(8, 11, 18) * 0.78f);

			string title = "还没有玩家聊天记录";
			Vector2 titleSize = FontAssets.MouseText.Value.MeasureString(title);
			Utils.DrawBorderString(spriteBatch, title, new Vector2(area.Center.X - titleSize.X * 0.45f, avatarRectangle.Bottom + 10), new Color(222, 228, 236), 0.9f);

			string detail = "玩家发送的聊天消息会显示在这里";
			Vector2 detailSize = FontAssets.MouseText.Value.MeasureString(detail) * 0.72f;
			Utils.DrawBorderString(spriteBatch, detail, new Vector2(area.Center.X - detailSize.X * 0.5f, avatarRectangle.Bottom + 38), new Color(166, 177, 190) * 0.9f, 0.72f);
		}

		private HistoryBubbleLayout CreateHistoryBubbleLayout(
			int messageIndex,
			ChatEntry message,
			int playerId,
			string displayName,
			Rectangle area,
			float y,
			float scale,
			int iconSize,
			int iconGap,
			int bubblePadding,
			int maximumBubbleWidth)
		{
			const int nameAreaHeight = 16;
			CachedMessageLayout cachedLayout = GetCachedMessageLayout(message, scale, iconSize, bubblePadding, maximumBubbleWidth);
			Rectangle iconRectangle;
			Rectangle bubbleRectangle;
			if (message.IsLocalPlayer)
			{
				iconRectangle = new Rectangle(area.Right - iconSize, (int)y + nameAreaHeight, iconSize, iconSize);
				bubbleRectangle = new Rectangle(iconRectangle.X - iconGap - cachedLayout.BubbleWidth, (int)y + nameAreaHeight, cachedLayout.BubbleWidth, cachedLayout.BubbleHeight);
			}
			else
			{
				iconRectangle = new Rectangle(area.X, (int)y + nameAreaHeight, iconSize, iconSize);
				bubbleRectangle = new Rectangle(iconRectangle.Right + iconGap, (int)y + nameAreaHeight, cachedLayout.BubbleWidth, cachedLayout.BubbleHeight);
			}

			Rectangle rowBounds = Rectangle.Union(iconRectangle, bubbleRectangle);
			Rectangle bounds = new(rowBounds.X, (int)y, rowBounds.Width, rowBounds.Height + nameAreaHeight);
			return new HistoryBubbleLayout(messageIndex, message, playerId, displayName, iconRectangle, bubbleRectangle, bounds, cachedLayout.Lines, cachedLayout.SelectableLines, cachedLayout.LineCount);
		}

		private CachedMessageLayout GetCachedMessageLayout(ChatEntry message, float scale, int iconSize, int bubblePadding, int maximumBubbleWidth)
		{
			if (messageLayoutCache.TryGetValue(message, out CachedMessageLayout cachedLayout) && cachedLayout.MaximumBubbleWidth == maximumBubbleWidth)
			{
				return cachedLayout;
			}

			int wrapWidth = (int)((maximumBubbleWidth - bubblePadding * 2) / scale);
			string[] lines = Utils.WordwrapString(message.Text, FontAssets.MouseText.Value, wrapWidth, MaximumWrappedLines, out int lineCount);
			List<EditableTextLine> selectableLines = CreateSelectableHistoryLines(message.Text, lines, lineCount);
			float maximumLineWidth = 0f;
			for (int index = 0; index < lineCount && index < lines.Length; index++)
			{
				if (lines[index] != null)
				{
					maximumLineWidth = Math.Max(maximumLineWidth, FontAssets.MouseText.Value.MeasureString(lines[index]).X * scale);
				}
			}

			const float timeScale = 0.56f;
			float timeWidth = FontAssets.MouseText.Value.MeasureString(message.Time).X * timeScale;
			int bubbleWidth = Math.Min(maximumBubbleWidth, Math.Max(72, (int)Math.Ceiling(Math.Max(maximumLineWidth, timeWidth) + bubblePadding * 2)));
			int bubbleHeight = Math.Max(iconSize, (int)Math.Ceiling(Math.Max(1, lineCount) * FontAssets.MouseText.Value.LineSpacing * scale + bubblePadding * 2 + 14));
			cachedLayout = new CachedMessageLayout(maximumBubbleWidth, lines, selectableLines, lineCount, bubbleWidth, bubbleHeight);
			messageLayoutCache[message] = cachedLayout;
			return cachedLayout;
		}

		private void RemoveStaleMessageLayouts(List<ChatEntry> messages)
		{
			if (messageLayoutCache.Count <= messages.Count)
			{
				return;
			}

			HashSet<ChatEntry> currentMessages = new(messages);
			List<ChatEntry> staleMessages = new();
			foreach (ChatEntry message in messageLayoutCache.Keys)
			{
				if (!currentMessages.Contains(message))
				{
					staleMessages.Add(message);
				}
			}
			foreach (ChatEntry message in staleMessages)
			{
				messageLayoutCache.Remove(message);
			}
		}

		private void DrawHistoryBubble(SpriteBatch spriteBatch, HistoryBubbleLayout layout, float scale, int bubblePadding)
		{
			bool isLocal = layout.Message.IsLocalPlayer;
			Color bubbleFill = isLocal ? new Color(6, 18, 15) * 0.74f : new Color(7, 10, 18) * 0.74f;
			Color bubbleBorder = isLocal ? new Color(84, 150, 128) * 0.76f : new Color(92, 122, 168) * 0.76f;
			DrawAvatarBadge(
				spriteBatch,
				layout.IconRectangle,
				layout.DisplayName,
				GetPlayerAvatar(layout.PlayerId),
				isLocal,
				isLocal ? new Color(5, 14, 12) * 0.82f : new Color(7, 10, 18) * 0.82f);

			DrawBox(spriteBatch, layout.BubbleRectangle, bubbleFill, bubbleBorder);
			DrawBubbleTail(spriteBatch, layout.BubbleRectangle, isLocal, bubbleFill);

			const float nameScale = 0.62f;
			Vector2 nameSize = FontAssets.MouseText.Value.MeasureString(layout.DisplayName);
			float fittedNameScale = Math.Min(nameScale, 96f / Math.Max(1f, nameSize.X));
			Vector2 namePosition = isLocal
				? new Vector2(layout.IconRectangle.Right, layout.IconRectangle.Y - 14)
				: new Vector2(layout.IconRectangle.X, layout.IconRectangle.Y - 14);
			Vector2 nameOrigin = isLocal ? new Vector2(nameSize.X, 0f) : Vector2.Zero;
			ChatManager.DrawColorCodedStringWithShadow(
				spriteBatch,
				FontAssets.MouseText.Value,
				layout.DisplayName,
				namePosition,
				isLocal ? new Color(244, 206, 92) : new Color(104, 174, 244),
				0f,
				nameOrigin,
				new Vector2(fittedNameScale));

			float lineHeight = FontAssets.MouseText.Value.LineSpacing * scale;
			for (int index = 0; index < layout.LineCount && index < layout.Lines.Length; index++)
			{
				if (layout.Lines[index] == null)
				{
					continue;
				}

				float lineY = layout.BubbleRectangle.Y + bubblePadding + index * lineHeight;
				if (index < layout.SelectableLines.Count && TryGetHistorySelectionRange(layout.MessageIndex, layout.Message.Text.Length, out int selectionStart, out int selectionEnd))
				{
					DrawEditableSelection(
						spriteBatch,
						TextureAssets.MagicPixel.Value,
						layout.SelectableLines[index],
						layout.BubbleRectangle.X + bubblePadding,
						lineY,
						lineHeight,
						scale,
						selectionStart,
						selectionEnd);
				}

				ChatManager.DrawColorCodedStringWithShadow(
					spriteBatch,
					FontAssets.MouseText.Value,
					layout.Lines[index],
					new Vector2(layout.BubbleRectangle.X + bubblePadding, lineY),
					Color.White,
					0f,
					Vector2.Zero,
					new Vector2(scale));
			}

			const float timeScale = 0.56f;
			Vector2 timeSize = FontAssets.MouseText.Value.MeasureString(layout.Message.Time) * timeScale;
			Vector2 timePosition = new(
				layout.BubbleRectangle.Right - bubblePadding - timeSize.X,
				layout.BubbleRectangle.Bottom - timeSize.Y - 4f);
			ChatManager.DrawColorCodedStringWithShadow(
				spriteBatch,
				FontAssets.MouseText.Value,
				layout.Message.Time,
				timePosition,
				new Color(126, 132, 142) * 0.78f,
				0f,
				Vector2.Zero,
				new Vector2(timeScale));
		}

		private static List<EditableTextLine> CreateSelectableHistoryLines(string text, string[] wrappedLines, int lineCount)
		{
			text ??= string.Empty;
			List<EditableTextLine> lines = new();
			int sourceIndex = 0;
			for (int index = 0; index < lineCount && index < wrappedLines.Length; index++)
			{
				string lineText = wrappedLines[index] ?? string.Empty;
				int lineStart = sourceIndex;
				if (lineText.Length > 0)
				{
					int matchIndex = text.IndexOf(lineText, sourceIndex, StringComparison.Ordinal);
					if (matchIndex >= 0)
					{
						lineStart = matchIndex;
					}
				}

				int lineEnd = Math.Min(text.Length, lineStart + lineText.Length);
				lines.Add(new EditableTextLine(lineText, lineStart, lineEnd));
				sourceIndex = lineEnd;
				while (sourceIndex < text.Length && (text[sourceIndex] == '\r' || text[sourceIndex] == '\n'))
				{
					sourceIndex++;
				}
			}

			return lines;
		}

		private static MessageHitTarget CreateMessageHitTarget(HistoryBubbleLayout layout)
		{
			const int buttonSize = 28;
			int buttonY = layout.BubbleRectangle.Center.Y - buttonSize / 2;
			Rectangle deleteRectangle = layout.Message.IsLocalPlayer
				? new Rectangle(layout.BubbleRectangle.X - buttonSize - 6, buttonY, buttonSize, buttonSize)
				: new Rectangle(layout.BubbleRectangle.Right + 6, buttonY, buttonSize, buttonSize);
			return new MessageHitTarget(layout.Message, layout.BubbleRectangle, deleteRectangle);
		}

		private void DrawMessageDeleteControl(SpriteBatch spriteBatch, MessageHitTarget target)
		{
			Point mouse = new(Main.mouseX, Main.mouseY);
			if (!target.BubbleRectangle.Contains(mouse) && !target.DeleteRectangle.Contains(mouse))
			{
				return;
			}

			bool hovered = target.DeleteRectangle.Contains(mouse);
			DrawCircle(spriteBatch, target.DeleteRectangle, hovered ? new Color(246, 72, 82) : new Color(196, 54, 66));
			Rectangle innerRectangle = target.DeleteRectangle;
			innerRectangle.Inflate(-2, -2);
			DrawCircle(spriteBatch, innerRectangle, hovered ? new Color(54, 16, 22) : new Color(24, 18, 24));

			const string deleteText = "X";
			Vector2 textSize = FontAssets.MouseText.Value.MeasureString(deleteText);
			const float textScale = 0.88f;
			ChatManager.DrawColorCodedStringWithShadow(
				spriteBatch,
				FontAssets.MouseText.Value,
				deleteText,
				target.DeleteRectangle.Center.ToVector2() + new Vector2(0f, 3f),
				hovered ? new Color(255, 112, 118) : new Color(240, 76, 88),
				0f,
				textSize * 0.5f,
				new Vector2(textScale));
		}

		private void UpdateInputScrollBarLayout()
		{
			int maximumFirstVisibleLine = GetMaximumInputFirstVisibleLine(out int lineCount, out int visibleLineCount);
			if (maximumFirstVisibleLine <= 0)
			{
				inputFirstVisibleLine = 0;
				inputScrollTrackRectangle = Rectangle.Empty;
				inputScrollThumbRectangle = Rectangle.Empty;
				return;
			}

			inputFirstVisibleLine = Math.Min(Math.Max(inputFirstVisibleLine, 0), maximumFirstVisibleLine);
			Rectangle inputBox = GetActiveInputRectangle();
			inputScrollTrackRectangle = new Rectangle(inputBox.Right - 10, inputBox.Y + 8, ScrollBarTrackWidth, inputBox.Height - 16);
			int thumbHeight = Math.Max(18, inputScrollTrackRectangle.Height * visibleLineCount / lineCount);
			int travel = Math.Max(1, inputScrollTrackRectangle.Height - thumbHeight);
			int thumbY = inputScrollTrackRectangle.Y + travel * inputFirstVisibleLine / maximumFirstVisibleLine;
			inputScrollThumbRectangle = new Rectangle(inputScrollTrackRectangle.Center.X - ScrollBarThumbWidth / 2, thumbY, ScrollBarThumbWidth, thumbHeight);
		}

		private void DrawInputScrollBar(SpriteBatch spriteBatch, TextInputTarget target)
		{
			if (inputTarget != target)
			{
				return;
			}

			UpdateInputScrollBarLayout();
			if (inputScrollTrackRectangle == Rectangle.Empty)
			{
				return;
			}

			bool hovered = GetInputScrollHitRectangle().Contains(new Point(Main.mouseX, Main.mouseY));
			Texture2D pixel = TextureAssets.MagicPixel.Value;
			spriteBatch.Draw(pixel, inputScrollTrackRectangle, hovered || inputScrollDragging ? new Color(104, 124, 154) * 0.78f : new Color(54, 66, 88) * 0.58f);
			spriteBatch.Draw(pixel, inputScrollThumbRectangle, inputScrollDragging ? new Color(210, 218, 214) : hovered ? new Color(176, 196, 202) : new Color(126, 146, 156));
		}

		private void HandleInputScroll(Point mouse)
		{
			int wheelDelta = PlayerInput.ScrollWheelDeltaForUI;
			int maximumFirstVisibleLine = GetMaximumInputFirstVisibleLine(out _, out _);
			if (wheelDelta == 0 || maximumFirstVisibleLine <= 0 || !GetActiveInputRectangle().Contains(mouse))
			{
				return;
			}

			int wheelNotches = Math.Max(1, Math.Abs(wheelDelta) / 120);
			inputFirstVisibleLine = Math.Min(
				Math.Max(inputFirstVisibleLine - Math.Sign(wheelDelta) * wheelNotches * 2, 0),
				maximumFirstVisibleLine);
			revealInputCursor = false;
			PlayerInput.ScrollWheelDelta = 0;
			PlayerInput.ScrollWheelDeltaForUI = 0;
		}

		private void HandleInputScrollBar(Point mouse)
		{
			int maximumFirstVisibleLine = GetMaximumInputFirstVisibleLine(out _, out _);
			if (maximumFirstVisibleLine <= 0 || inputScrollTrackRectangle == Rectangle.Empty)
			{
				inputScrollDragging = false;
				return;
			}

			if (!inputScrollDragging)
			{
				Rectangle hitRectangle = GetInputScrollHitRectangle();
				bool leftPressed = Main.mouseLeft && (Main.mouseLeftRelease || hitRectangle.Contains(mouse));
				if (!hitRectangle.Contains(mouse) || !leftPressed)
				{
					return;
				}

				inputScrollDragging = true;
				if (!inputScrollThumbRectangle.Contains(mouse))
				{
					inputFirstVisibleLine = GetInputScrollLineFromTrack(mouse.Y, maximumFirstVisibleLine);
				}
				inputScrollDragStartY = mouse.Y;
				inputScrollDragStartLine = inputFirstVisibleLine;
				revealInputCursor = false;
				Main.mouseLeftRelease = false;
			}

			if (!Main.mouseLeft)
			{
				inputScrollDragging = false;
				return;
			}

			int travel = Math.Max(1, inputScrollTrackRectangle.Height - inputScrollThumbRectangle.Height);
			inputFirstVisibleLine = Math.Min(
				Math.Max(inputScrollDragStartLine + (int)Math.Round((mouse.Y - inputScrollDragStartY) * maximumFirstVisibleLine / (float)travel), 0),
				maximumFirstVisibleLine);
			revealInputCursor = false;
		}

		private int GetInputScrollLineFromTrack(int mouseY, int maximumFirstVisibleLine)
		{
			int travel = Math.Max(1, inputScrollTrackRectangle.Height - inputScrollThumbRectangle.Height);
			float ratio = (mouseY - inputScrollTrackRectangle.Y - inputScrollThumbRectangle.Height / 2f) / travel;
			return Math.Min(Math.Max((int)Math.Round(ratio * maximumFirstVisibleLine), 0), maximumFirstVisibleLine);
		}

		private int GetMaximumInputFirstVisibleLine(out int lineCount, out int visibleLineCount)
		{
			if (inputTarget == TextInputTarget.None)
			{
				lineCount = 0;
				visibleLineCount = 0;
				return 0;
			}

			Rectangle textArea = GetTextArea();
			float scale = GetActiveInputTextScale();
			lineCount = WrapEditableText(inputText ?? string.Empty, textArea, scale).Count;
			visibleLineCount = Math.Max(1, (int)(textArea.Height / (FontAssets.MouseText.Value.LineSpacing * scale)));
			return Math.Max(0, lineCount - visibleLineCount);
		}

		private void DrawScrollBar(SpriteBatch spriteBatch)
		{
			if (maximumScrollPixels <= 0f)
			{
				scrollTrackRectangle = Rectangle.Empty;
				scrollThumbRectangle = Rectangle.Empty;
				return;
			}

			Texture2D pixel = TextureAssets.MagicPixel.Value;
			scrollTrackRectangle = new Rectangle(historyRectangle.Right - 10, historyRectangle.Y + 8, ScrollBarTrackWidth, historyRectangle.Height - 16);
			bool hovered = GetScrollHitRectangle().Contains(new Point(Main.mouseX, Main.mouseY));
			spriteBatch.Draw(pixel, scrollTrackRectangle, hovered || scrollDragging ? new Color(104, 124, 154) * 0.78f : new Color(54, 66, 88) * 0.58f);

			Rectangle area = GetHistoryArea();
			int thumbHeight = Math.Max(18, (int)(scrollTrackRectangle.Height * area.Height / (area.Height + maximumScrollPixels)));
			int travel = Math.Max(1, scrollTrackRectangle.Height - thumbHeight);
			int thumbY = scrollTrackRectangle.Y + (int)(travel * (scrollPixels / maximumScrollPixels));
			scrollThumbRectangle = new Rectangle(scrollTrackRectangle.Center.X - ScrollBarThumbWidth / 2, thumbY, ScrollBarThumbWidth, thumbHeight);
			spriteBatch.Draw(pixel, scrollThumbRectangle, scrollDragging ? new Color(210, 218, 214) : hovered ? new Color(176, 196, 202) : new Color(126, 146, 156));
		}

		private void HandleScroll(Point mouse)
		{
			int wheelDelta = PlayerInput.ScrollWheelDeltaForUI;
			if (wheelDelta == 0 || !historyRectangle.Contains(mouse) || maximumScrollPixels <= 0f)
			{
				return;
			}

			stickToBottom = false;
			targetScrollPixels = MathHelper.Clamp(targetScrollPixels - wheelDelta * MouseWheelScrollScale, 0f, maximumScrollPixels);
			PlayerInput.ScrollWheelDelta = 0;
			PlayerInput.ScrollWheelDeltaForUI = 0;
		}

		private void HandleScrollBar(Point mouse)
		{
			if (maximumScrollPixels <= 0f || scrollTrackRectangle == Rectangle.Empty)
			{
				scrollDragging = false;
				return;
			}

			if (!scrollDragging)
			{
				Rectangle hitRectangle = GetScrollHitRectangle();
				bool leftPressed = Main.mouseLeft && (Main.mouseLeftRelease || hitRectangle.Contains(mouse));
				if (!hitRectangle.Contains(mouse) || !leftPressed)
				{
					return;
				}

				scrollDragging = true;
				if (!scrollThumbRectangle.Contains(mouse))
				{
					targetScrollPixels = MathHelper.Clamp(GetScrollTargetFromTrack(mouse.Y), 0f, maximumScrollPixels);
					scrollPixels = targetScrollPixels;
				}
				scrollDragStartY = mouse.Y;
				scrollDragStartTarget = targetScrollPixels;
				Main.mouseLeftRelease = false;
			}

			if (!scrollDragging)
			{
				return;
			}

			if (!Main.mouseLeft)
			{
				scrollDragging = false;
				return;
			}

			int travel = Math.Max(1, scrollTrackRectangle.Height - scrollThumbRectangle.Height);
			targetScrollPixels = MathHelper.Clamp(scrollDragStartTarget + (mouse.Y - scrollDragStartY) * maximumScrollPixels / travel, 0f, maximumScrollPixels);
			stickToBottom = false;
		}

		private float GetScrollTargetFromTrack(int mouseY)
		{
			int travel = Math.Max(1, scrollTrackRectangle.Height - scrollThumbRectangle.Height);
			float ratio = (mouseY - scrollTrackRectangle.Y - scrollThumbRectangle.Height / 2f) / travel;
			return ratio * maximumScrollPixels;
		}

		private void UpdateSmoothScroll()
		{
			targetScrollPixels = MathHelper.Clamp(targetScrollPixels, 0f, maximumScrollPixels);
			if (Math.Abs(scrollPixels - targetScrollPixels) < 0.35f)
			{
				scrollPixels = targetScrollPixels;
				return;
			}

			scrollPixels = MathHelper.Lerp(scrollPixels, targetScrollPixels, SmoothScrollLerp);
		}

		private void SubmitMessage()
		{
			string messageText = inputText.Trim();
			if (string.IsNullOrEmpty(messageText))
			{
				return;
			}

			ChatMessage message = ChatManager.Commands.CreateOutgoingMessage(messageText);
			if (Main.netMode == Terraria.ID.NetmodeID.MultiplayerClient)
			{
				ChatHelper.SendChatMessageFromClient(message);
			}
			else if (Main.netMode == Terraria.ID.NetmodeID.SinglePlayer)
			{
				ChatManager.Commands.ProcessIncomingMessage(message, Main.myPlayer);
			}

			inputText = string.Empty;
			chatInputDraft = string.Empty;
			inputFirstVisibleLine = 0;
			MoveCursor(0, false);
			stickToBottom = true;
			Main.clrInput();
		}

		private void OpenClearHistoryConfirmation()
		{
			TerraChatPlayer chatPlayer = Main.LocalPlayer.GetModPlayer<TerraChatPlayer>();
			if (chatPlayer.History.Count == 0)
			{
				return;
			}

			SaveActiveInputDraft();
			InputFocused = false;
			inputMouseSelecting = false;
			clearHistoryConfirmationOpen = true;
		}

		private void ConfirmClearChatHistory()
		{
			TerraChatPlayer chatPlayer = Main.LocalPlayer.GetModPlayer<TerraChatPlayer>();
			chatPlayer.History.Clear();
			messageLayoutCache.Clear();
			clearHistoryConfirmationOpen = false;
			scrollPixels = 0f;
			targetScrollPixels = 0f;
			maximumScrollPixels = 0f;
			stickToBottom = true;
			visibleAvatarTargets.Clear();
			visibleMessageTargets.Clear();
		}

		private void DeleteMessage(ChatEntry message)
		{
			TerraChatPlayer chatPlayer = Main.LocalPlayer.GetModPlayer<TerraChatPlayer>();
			bool wasAtBottom = maximumScrollPixels <= 0f || Math.Abs(targetScrollPixels - maximumScrollPixels) < 1f;
			if (!chatPlayer.History.Remove(message))
			{
				return;
			}

			messageLayoutCache.Remove(message);
			visibleMessageTargets.Clear();
			if (wasAtBottom)
			{
				stickToBottom = true;
			}
		}

		private void ChooseAvatar()
		{
			if (fileDialogOpen)
			{
				return;
			}

			fileDialogOpen = true;
			fileDialogCompleted = false;
			selectedImagePath = null;
			fileDialogException = null;
			IntPtr ownerHandle = AvatarStorage.GetDialogOwnerHandle();
			Thread dialogThread = new(() =>
			{
				try
				{
					if (AvatarStorage.TryChooseImagePath(ownerHandle, out string path))
					{
						selectedImagePath = path;
					}
				}
				catch (Exception exception)
				{
					fileDialogException = exception;
				}
				finally
				{
					fileDialogCompleted = true;
				}
			});
			dialogThread.IsBackground = true;
			if (OperatingSystem.IsWindows())
			{
				dialogThread.SetApartmentState(ApartmentState.STA);
				dialogThread.Start();
			}
			else
			{
				fileDialogOpen = false;
			}
		}

		private void ProcessFileDialogCompletion()
		{
			if (!fileDialogOpen || !fileDialogCompleted)
			{
				return;
			}

			fileDialogOpen = false;
			fileDialogCompleted = false;
			if (fileDialogException != null)
			{
				ModContent.GetInstance<TerraChat>().Logger.Warn("无法打开头像文件选择器", fileDialogException);
				fileDialogException = null;
				return;
			}
			if (string.IsNullOrEmpty(selectedImagePath))
			{
				return;
			}

			try
			{
				using FileStream stream = File.OpenRead(selectedImagePath);
				Texture2D texture = Texture2D.FromStream(Main.graphics.GraphicsDevice, stream);
				cropSource?.Dispose();
				cropSource = texture;
				cropSourcePixels = new Color[texture.Width * texture.Height];
				texture.GetData(cropSourcePixels);
				cropZoom = 1f;
				cropPan = Vector2.Zero;
				cropDragging = false;
			}
			catch (Exception exception)
			{
				ModContent.GetInstance<TerraChat>().Logger.Warn("无法加载所选头像", exception);
			}
			finally
			{
				selectedImagePath = null;
			}
		}

		private void EnsureAvatarLoaded()
		{
			if (avatarLoadAttempted)
			{
				return;
			}

			avatarLoadAttempted = true;
			try
			{
				Texture2D source = AvatarStorage.LoadTexture();
				if (source != null)
				{
					if (source.Width == AvatarStorage.AvatarTextureSize && source.Height == AvatarStorage.AvatarTextureSize)
					{
						localAvatar = source;
					}
					else
					{
						localAvatar = AvatarStorage.CreateCircularAvatar(source, 1f, Vector2.Zero);
						source.Dispose();
					}
					localDisplayAvatar = AvatarStorage.CreateDisplayAvatar(localAvatar);
				}
			}
			catch (Exception exception)
			{
				ModContent.GetInstance<TerraChat>().Logger.Warn("无法读取已保存的头像", exception);
			}
		}

		internal void TrySyncLocalProfile()
		{
			if (!LocalProfileSyncPending || Main.myPlayer < 0 || Main.myPlayer >= Main.maxPlayers)
			{
				return;
			}

			EnsureAvatarLoaded();
			TerraChatPlayer profile = Main.LocalPlayer.GetModPlayer<TerraChatPlayer>();
			profile.NetworkAvatarData = localAvatar == null
				? []
				: AvatarStorage.CreateNetworkAvatarBytes(localAvatar);
			LocalProfileSyncPending = false;
			TerraChat.SendProfile(Main.myPlayer);
		}

		internal void SetNetworkAvatar(int playerId, byte[] avatarData)
		{
			if (networkAvatars.Remove(playerId, out Texture2D previousAvatar))
			{
				previousAvatar.Dispose();
			}
			if (avatarData == null || avatarData.Length == 0 || playerId == Main.myPlayer)
			{
				return;
			}

			try
			{
				using MemoryStream stream = new(avatarData, false);
				networkAvatars[playerId] = Texture2D.FromStream(Main.graphics.GraphicsDevice, stream);
			}
			catch (Exception exception)
			{
				ModContent.GetInstance<TerraChat>().Logger.Warn("无法读取远程玩家头像", exception);
			}
		}

		private Texture2D GetPlayerAvatar(int playerId)
		{
			if (playerId == Main.myPlayer)
			{
				return localDisplayAvatar ?? localAvatar;
			}
			return networkAvatars.TryGetValue(playerId, out Texture2D avatar) ? avatar : null;
		}

		private static int ResolvePlayerId(ChatEntry message)
		{
			if (message.IsLocalPlayer)
			{
				return Main.myPlayer;
			}
			if (message.SenderPlayerId >= 0 && message.SenderPlayerId < Main.maxPlayers)
			{
				Player savedSender = Main.player[message.SenderPlayerId];
				if (savedSender.active && (savedSender.name == message.Sender || savedSender.GetModPlayer<TerraChatPlayer>().GetDisplayName() == message.Sender))
				{
					return message.SenderPlayerId;
				}
			}

			for (int playerId = 0; playerId < Main.maxPlayers; playerId++)
			{
				Player player = Main.player[playerId];
				if (player.active && (player.name == message.Sender || player.GetModPlayer<TerraChatPlayer>().GetDisplayName() == message.Sender))
				{
					return playerId;
				}
			}
			return -1;
		}

		private static string GetPlayerDisplayName(int playerId, string fallback)
		{
			if (playerId >= 0 && playerId < Main.maxPlayers)
			{
				return Main.player[playerId].GetModPlayer<TerraChatPlayer>().GetDisplayName();
			}
			return fallback;
		}

		private void DrawAvatarBadge(SpriteBatch spriteBatch, Rectangle rectangle, string name, Texture2D texture, bool isLocal, Color fill)
		{
			DrawCircle(spriteBatch, rectangle, Color.Black * 0.94f);
			Rectangle innerRectangle = new(rectangle.X + 1, rectangle.Y + 1, rectangle.Width - 2, rectangle.Height - 2);
			DrawCircle(spriteBatch, innerRectangle, fill);
			if (texture != null && !texture.IsDisposed)
			{
				spriteBatch.Draw(texture, innerRectangle, Color.White);
				return;
			}

			string initial = string.IsNullOrWhiteSpace(name) ? "?" : name.Substring(0, 1).ToUpperInvariant();
			Vector2 size = FontAssets.MouseText.Value.MeasureString(initial);
			Utils.DrawBorderString(
				spriteBatch,
				initial,
				new Vector2(rectangle.Center.X - size.X * 0.5f, rectangle.Center.Y - size.Y * 0.5f),
				isLocal ? new Color(212, 242, 228) : new Color(216, 226, 246));
		}

		private void HandleMouseInput()
		{
			Point mouse = new(Main.mouseX, Main.mouseY);
			if (cropSource != null)
			{
				HandleCropDialog(mouse);
				return;
			}
			if (profileCardOpen)
			{
				HandleProfileCardMouse(mouse);
				return;
			}
			if (clearHistoryConfirmationOpen)
			{
				HandleClearHistoryConfirmation(mouse);
				return;
			}

			if (!panelRectangle.Contains(mouse))
			{
				if (Main.mouseLeft && Main.mouseLeftRelease)
				{
					Main.mouseLeftRelease = false;
					ClearHistorySelection();
					if (InputFocused)
					{
						InputFocused = false;
						inputMouseSelecting = false;
						MoveCursor(inputCursorIndex, false);
					}
				}
				return;
			}

			Main.LocalPlayer.mouseInterface = true;
			if (!Main.mouseLeft || !Main.mouseLeftRelease)
			{
				return;
			}

			if (TryBeginHistoryMouseSelection(mouse))
			{
				return;
			}

			Main.mouseLeftRelease = false;
			ClearHistorySelection();
			if (clearHistoryButtonRectangle.Contains(mouse))
			{
				OpenClearHistoryConfirmation();
				return;
			}

			foreach (MessageHitTarget target in visibleMessageTargets)
			{
				if (target.DeleteRectangle.Contains(mouse))
				{
					DeleteMessage(target.Message);
					return;
				}
			}

			if (closeButtonRectangle.Contains(mouse))
			{
				inputMouseSelecting = false;
				owner.Close();
				return;
			}

			if (sendButtonRectangle.Contains(mouse))
			{
				inputMouseSelecting = false;
				SubmitMessage();
				return;
			}

			if (headerAvatarRectangle.Contains(mouse))
			{
				InputFocused = false;
				inputMouseSelecting = false;
				OpenProfileCard(Main.myPlayer);
				return;
			}

			foreach (AvatarHitTarget target in visibleAvatarTargets)
			{
				if (target.Rectangle.Contains(mouse))
				{
					InputFocused = false;
					inputMouseSelecting = false;
					OpenProfileCard(target.PlayerId);
					return;
				}
			}

			if (inputRectangle.Contains(mouse))
			{
				InputFocused = true;
				inputMouseSelecting = true;
				MoveCursor(GetCursorIndexFromMouse(mouse), IsShiftDown(Main.keyState));
				Main.clrInput();
				return;
			}

			if (InputFocused)
			{
				InputFocused = false;
				inputMouseSelecting = false;
				MoveCursor(inputCursorIndex, false);
			}
		}

		private void HandleHistoryMouseSelection(Point mouse)
		{
			if (historyMouseSelecting)
			{
				if (!Main.mouseLeft)
				{
					historyMouseSelecting = false;
					return;
				}

				if (TryGetHistorySelectionPoint(mouse, true, out HistorySelectionPoint activePoint))
				{
					historySelectionActive = activePoint;
				}
				Main.LocalPlayer.mouseInterface = true;
				return;
			}

			if (!Main.mouseLeft || !Main.mouseLeftRelease)
			{
				return;
			}

			TryBeginHistoryMouseSelection(mouse);
		}

		private bool TryBeginHistoryMouseSelection(Point mouse)
		{
			if (!GetHistoryArea().Contains(mouse) || GetScrollHitRectangle().Contains(mouse))
			{
				return false;
			}

			if (!TryGetHistorySelectionPoint(mouse, false, out HistorySelectionPoint startPoint))
			{
				return false;
			}

			InputFocused = false;
			inputMouseSelecting = false;
			historyMouseSelecting = true;
			historySelectionAnchor = startPoint;
			historySelectionActive = startPoint;
			Main.mouseLeftRelease = false;
			Main.clrInput();
			return true;
		}

		private bool TryGetHistorySelectionPoint(Point mouse, bool allowNearest, out HistorySelectionPoint point)
		{
			HistoryBubbleLayout target = null;
			foreach (HistoryBubbleLayout layout in historySelectionLayouts)
			{
				if (layout.BubbleRectangle.Contains(mouse))
				{
					target = layout;
					break;
				}
			}

			if (target == null && allowNearest && historySelectionLayouts.Count > 0)
			{
				int bestDistance = int.MaxValue;
				foreach (HistoryBubbleLayout layout in historySelectionLayouts)
				{
					int distance = mouse.Y < layout.BubbleRectangle.Y
						? layout.BubbleRectangle.Y - mouse.Y
						: Math.Max(0, mouse.Y - layout.BubbleRectangle.Bottom);
					if (distance < bestDistance)
					{
						bestDistance = distance;
						target = layout;
					}
				}
			}

			if (target == null || target.SelectableLines.Count == 0)
			{
				point = HistorySelectionPoint.Empty;
				return false;
			}

			const float scale = 0.85f;
			const int bubblePadding = 10;
			float lineHeight = FontAssets.MouseText.Value.LineSpacing * scale;
			int lineIndex = Math.Min(Math.Max((int)((mouse.Y - target.BubbleRectangle.Y - bubblePadding) / lineHeight), 0), target.SelectableLines.Count - 1);
			EditableTextLine line = target.SelectableLines[lineIndex];
			float horizontalPosition = (mouse.X - target.BubbleRectangle.X - bubblePadding) / scale;
			point = new HistorySelectionPoint(target.MessageIndex, FindEditableIndexAtPosition(line, horizontalPosition));
			return true;
		}

		private void HandleInputMouseSelection(Point mouse)
		{
			if (!inputMouseSelecting)
			{
				return;
			}

			if (!Main.mouseLeft)
			{
				inputMouseSelecting = false;
				return;
			}

			InputFocused = true;
			Main.LocalPlayer.mouseInterface = true;
			MoveCursor(GetCursorIndexFromMouse(mouse), true);
		}

		private void HandleClearHistoryConfirmation(Point mouse)
		{
			Main.LocalPlayer.mouseInterface = true;
			if (!Main.mouseLeft || !Main.mouseLeftRelease)
			{
				return;
			}

			Main.mouseLeftRelease = false;
			if (clearHistoryConfirmRectangle.Contains(mouse))
			{
				ConfirmClearChatHistory();
			}
			else if (clearHistoryCancelRectangle.Contains(mouse))
			{
				clearHistoryConfirmationOpen = false;
			}
		}

		private void HandleProfileCardMouse(Point mouse)
		{
			bool isOwnProfile = profileTargetPlayerId == Main.myPlayer;
			if (!Main.mouseLeft || !Main.mouseLeftRelease)
			{
				return;
			}

			Main.mouseLeftRelease = false;
			if (!profileCardRectangle.Contains(mouse)
				|| (isOwnProfile && profileCancelRectangle.Contains(mouse))
				|| (!isOwnProfile && GetReadOnlyCloseRectangle().Contains(mouse)))
			{
				CloseProfileCard(false);
				return;
			}

			if (profileSaveRectangle.Contains(mouse))
			{
				CloseProfileCard(isOwnProfile);
				return;
			}
			if (!isOwnProfile)
			{
				return;
			}

			if (profileAvatarRectangle.Contains(mouse))
			{
				SaveActiveInputDraft();
				InputFocused = false;
				ChooseAvatar();
				return;
			}

			if (profileNameRectangle.Contains(mouse))
			{
				SwitchInputTarget(TextInputTarget.ProfileName);
				MoveCursor(GetCursorIndexFromMouse(mouse), IsShiftDown(Main.keyState));
				inputMouseSelecting = true;
				Main.clrInput();
				return;
			}

			if (profileDescriptionRectangle.Contains(mouse))
			{
				SwitchInputTarget(TextInputTarget.ProfileDescription);
				MoveCursor(GetCursorIndexFromMouse(mouse), IsShiftDown(Main.keyState));
				inputMouseSelecting = true;
				Main.clrInput();
				return;
			}

			SaveActiveInputDraft();
			InputFocused = false;
			inputMouseSelecting = false;
		}

		private void OpenProfileCard(int playerId)
		{
			if (playerId < 0 || playerId >= Main.maxPlayers)
			{
				return;
			}
			SaveActiveInputDraft();
			profileTargetPlayerId = playerId;
			TerraChatPlayer targetProfile = Main.player[playerId].GetModPlayer<TerraChatPlayer>();
			profileNameDraft = targetProfile.GetDisplayName();
			profileDescriptionDraft = targetProfile.Description;
			profileCardOpen = true;
			inputTarget = TextInputTarget.None;
			inputText = string.Empty;
			InputFocused = false;
			inputMouseSelecting = false;
		}

		private void CloseProfileCard(bool save)
		{
			SaveActiveInputDraft();
			if (save && profileTargetPlayerId == Main.myPlayer)
			{
				TerraChatPlayer chatPlayer = Main.LocalPlayer.GetModPlayer<TerraChatPlayer>();
				string profileName = profileNameDraft.Replace("\r", " ").Replace("\n", " ").Trim();
				if (profileName.Length > TerraChatPlayer.MaximumProfileNameLength)
				{
					profileName = profileName.Substring(0, TerraChatPlayer.MaximumProfileNameLength);
				}
				chatPlayer.ProfileName = profileName;
				chatPlayer.Description = profileDescriptionDraft.Length > TerraChatPlayer.MaximumDescriptionLength
					? profileDescriptionDraft.Substring(0, TerraChatPlayer.MaximumDescriptionLength)
					: profileDescriptionDraft;
				TerraChat.SendProfile(Main.myPlayer);
			}

			profileCardOpen = false;
			profileTargetPlayerId = -1;
			inputTarget = TextInputTarget.Chat;
			inputText = chatInputDraft;
			InputFocused = false;
			inputMouseSelecting = false;
			inputScrollDragging = false;
			inputFirstVisibleLine = 0;
			MoveCursor(inputText.Length, false);
		}

		private void SwitchInputTarget(TextInputTarget target)
		{
			SaveActiveInputDraft();
			inputTarget = target;
			inputText = target switch
			{
				TextInputTarget.Chat => chatInputDraft,
				TextInputTarget.ProfileName => profileNameDraft,
				TextInputTarget.ProfileDescription => profileDescriptionDraft,
				_ => string.Empty
			};
			InputFocused = target != TextInputTarget.None;
			inputMouseSelecting = false;
			inputScrollDragging = false;
			inputFirstVisibleLine = 0;
			MoveCursor(inputText.Length, false);
			lastCaptureKeyState = Main.keyState;
		}

		private void SaveActiveInputDraft()
		{
			switch (inputTarget)
			{
				case TextInputTarget.Chat:
					chatInputDraft = inputText;
					break;
				case TextInputTarget.ProfileName:
					profileNameDraft = inputText;
					break;
				case TextInputTarget.ProfileDescription:
					profileDescriptionDraft = inputText;
					break;
			}
		}

		private string GetProfileFieldText(TextInputTarget target)
		{
			if (profileCardOpen && profileTargetPlayerId != Main.myPlayer)
			{
				TerraChatPlayer targetProfile = GetProfileTarget();
				return target == TextInputTarget.ProfileName ? targetProfile.GetDisplayName() : targetProfile.Description;
			}
			if (inputTarget == target)
			{
				return inputText;
			}
			return target == TextInputTarget.ProfileName ? profileNameDraft : profileDescriptionDraft;
		}

		private TerraChatPlayer GetProfileTarget()
		{
			int playerId = profileTargetPlayerId >= 0 && profileTargetPlayerId < Main.maxPlayers
				? profileTargetPlayerId
				: Main.myPlayer;
			return Main.player[playerId].GetModPlayer<TerraChatPlayer>();
		}

		private Rectangle GetReadOnlyCloseRectangle()
		{
			return new Rectangle(profileCardRectangle.Center.X - 52, profileCardRectangle.Bottom - 46, 104, 32);
		}

		private int GetMaximumInputLength()
		{
			return inputTarget switch
			{
				TextInputTarget.ProfileName => TerraChatPlayer.MaximumProfileNameLength,
				TextInputTarget.ProfileDescription => TerraChatPlayer.MaximumDescriptionLength,
				_ => MaximumInputLength
			};
		}

		private void DrawImePanel(SpriteBatch spriteBatch)
		{
			if (!InputFocused || cropSource != null)
			{
				return;
			}

			spriteBatch.End();
			Rectangle activeInputRectangle = GetActiveInputRectangle();
			Main.instance.SetIMEPanelAnchor(new Vector2(activeInputRectangle.X + 12f, activeInputRectangle.Bottom - 32f), 0f);
			Main.instance.DrawIMEPanel();
			spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, null, null, null, null, Main.UIScaleMatrix);
		}

		private void HandleCropDialog(Point mouse)
		{
			int wheelDelta = PlayerInput.ScrollWheelDeltaForUI;
			if (wheelDelta != 0 && cropPreviewRectangle.Contains(mouse))
			{
				SetCropZoom(cropZoom + wheelDelta / 120f * 0.12f);
				PlayerInput.ScrollWheelDelta = 0;
				PlayerInput.ScrollWheelDeltaForUI = 0;
			}

			if (cropDragging)
			{
				if (!Main.mouseLeft)
				{
					cropDragging = false;
					return;
				}

				Vector2 delta = new Vector2(mouse.X - cropDragLastMouse.X, mouse.Y - cropDragLastMouse.Y)
					* AvatarStorage.AvatarTextureSize / cropPreviewRectangle.Width;
				if (delta != Vector2.Zero)
				{
					cropPan = AvatarStorage.ClampPan(cropSource, cropZoom, cropPan + delta);
					cropDragLastMouse = mouse;
				}
				return;
			}

			if (!Main.mouseLeft || !Main.mouseLeftRelease)
			{
				return;
			}
			Main.mouseLeftRelease = false;

			if (cropPreviewRectangle.Contains(mouse))
			{
				cropDragging = true;
				cropDragLastMouse = mouse;
				return;
			}

			if (cropMinusRectangle.Contains(mouse))
			{
				SetCropZoom(cropZoom - 0.15f);
				return;
			}

			if (cropPlusRectangle.Contains(mouse))
			{
				SetCropZoom(cropZoom + 0.15f);
				return;
			}

			Rectangle zoomTrack = GetCropZoomTrackRectangle();
			if (zoomTrack.Contains(mouse))
			{
				SetCropZoom(1f + 3f * (mouse.X - zoomTrack.X) / zoomTrack.Width);
				return;
			}

			if (cropCancelRectangle.Contains(mouse))
			{
				CloseCropDialog();
				return;
			}

			if (cropConfirmRectangle.Contains(mouse))
			{
				ConfirmCrop();
			}
		}

		private void DrawCropDialog(SpriteBatch spriteBatch)
		{
			spriteBatch.Draw(TextureAssets.MagicPixel.Value, new Rectangle(0, 0, Main.screenWidth, Main.screenHeight), Color.Black * 0.52f);
			DrawBox(spriteBatch, cropDialogRectangle, new Color(6, 8, 13) * 0.98f, new Color(126, 146, 178) * 0.9f);
			Utils.DrawBorderString(spriteBatch, "裁切头像", new Vector2(cropDialogRectangle.X + 18, cropDialogRectangle.Y + 15), Color.White, 1f);

			DrawCircle(spriteBatch, cropPreviewRectangle, Color.Black * 0.94f);
			Rectangle previewInner = cropPreviewRectangle;
			previewInner.Inflate(-2, -2);
			DrawLiveCropPreview(spriteBatch, previewInner);

			DrawButton(spriteBatch, cropMinusRectangle, "－");
			DrawButton(spriteBatch, cropPlusRectangle, "＋");
			Rectangle zoomTrack = GetCropZoomTrackRectangle();
			spriteBatch.Draw(TextureAssets.MagicPixel.Value, zoomTrack, new Color(58, 70, 92));
			float ratio = (cropZoom - 1f) / 3f;
			Rectangle knob = new(zoomTrack.X + (int)(zoomTrack.Width * ratio) - 6, zoomTrack.Center.Y - 6, 12, 12);
			DrawCircle(spriteBatch, knob, new Color(180, 202, 210));

			string hint = "拖动图片调整位置，滚轮或 －/＋ 缩放";
			Vector2 hintSize = FontAssets.MouseText.Value.MeasureString(hint) * 0.72f;
			Utils.DrawBorderString(spriteBatch, hint, new Vector2(cropDialogRectangle.Center.X - hintSize.X * 0.5f, cropMinusRectangle.Bottom + 10), new Color(166, 177, 190), 0.72f);
			DrawButton(spriteBatch, cropCancelRectangle, "取消");
			DrawButton(spriteBatch, cropConfirmRectangle, "确认");
		}

		private void DrawLiveCropPreview(SpriteBatch spriteBatch, Rectangle previewRectangle)
		{
			if (cropSource == null || cropSource.IsDisposed)
			{
				return;
			}

			inverseCircleTexture ??= CreateInverseCircleTexture();
			GraphicsDevice graphicsDevice = spriteBatch.GraphicsDevice;
			Rectangle oldScissorRectangle = graphicsDevice.ScissorRectangle;
			spriteBatch.End();
			Rectangle previewScissorRectangle = GetScaledScissorRectangle(previewRectangle);
			previewScissorRectangle.Inflate(-1, -1);
			graphicsDevice.ScissorRectangle = previewScissorRectangle;
			spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.LinearClamp, null, ScissorRasterizerState, null, Main.UIScaleMatrix);

			Color background = new(5, 8, 14);
			spriteBatch.Draw(TextureAssets.MagicPixel.Value, previewRectangle, background);
			float cropScale = Math.Max(AvatarStorage.AvatarTextureSize / (float)cropSource.Width, AvatarStorage.AvatarTextureSize / (float)cropSource.Height) * cropZoom;
			float previewScale = previewRectangle.Width / (float)AvatarStorage.AvatarTextureSize;
			int width = Math.Max(1, (int)Math.Round(cropSource.Width * cropScale * previewScale));
			int height = Math.Max(1, (int)Math.Round(cropSource.Height * cropScale * previewScale));
			int centerX = previewRectangle.Center.X + (int)Math.Round(cropPan.X * previewScale);
			int centerY = previewRectangle.Center.Y + (int)Math.Round(cropPan.Y * previewScale);
			Rectangle destination = new(centerX - width / 2, centerY - height / 2, width, height);
			spriteBatch.Draw(cropSource, destination, Color.White);
			spriteBatch.Draw(inverseCircleTexture, previewRectangle, background);

			spriteBatch.End();
			graphicsDevice.ScissorRectangle = oldScissorRectangle;
			spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, null, null, null, null, Main.UIScaleMatrix);
		}

		private void SetCropZoom(float zoom)
		{
			float newZoom = MathHelper.Clamp(zoom, 1f, 4f);
			if (Math.Abs(newZoom - cropZoom) < 0.001f)
			{
				return;
			}

			cropZoom = newZoom;
			cropPan = AvatarStorage.ClampPan(cropSource, cropZoom, cropPan);
		}

		private void ConfirmCrop()
		{
			Texture2D confirmedAvatar = null;
			try
			{
				confirmedAvatar = AvatarStorage.CreateCircularAvatar(cropSource, cropSourcePixels, cropZoom, cropPan);
				AvatarStorage.SaveTexture(confirmedAvatar);
				localAvatar?.Dispose();
				localAvatar = confirmedAvatar;
				confirmedAvatar = null;
				localDisplayAvatar?.Dispose();
				localDisplayAvatar = AvatarStorage.CreateDisplayAvatar(localAvatar);
				TerraChatPlayer profile = Main.LocalPlayer.GetModPlayer<TerraChatPlayer>();
				profile.NetworkAvatarData = AvatarStorage.CreateNetworkAvatarBytes(localAvatar);
				TerraChat.SendProfile(Main.myPlayer);
				cropSource.Dispose();
				cropSource = null;
				cropSourcePixels = null;
			}
			catch (Exception exception)
			{
				confirmedAvatar?.Dispose();
				ModContent.GetInstance<TerraChat>().Logger.Warn("无法保存头像", exception);
			}
		}

		private void CloseCropDialog()
		{
			cropSource?.Dispose();
			cropSource = null;
			cropSourcePixels = null;
			cropDragging = false;
		}

		private Rectangle GetCropZoomTrackRectangle()
		{
			return new Rectangle(cropMinusRectangle.Right + 12, cropMinusRectangle.Center.Y - 2, cropPlusRectangle.X - cropMinusRectangle.Right - 24, 4);
		}

		private static void DrawBubbleTail(SpriteBatch spriteBatch, Rectangle bubbleRectangle, bool isLocal, Color fill)
		{
			Rectangle tail = isLocal
				? new Rectangle(bubbleRectangle.Right - 2, bubbleRectangle.Y + 14, 8, 8)
				: new Rectangle(bubbleRectangle.X - 6, bubbleRectangle.Y + 14, 8, 8);
			spriteBatch.Draw(TextureAssets.MagicPixel.Value, tail, fill);
		}

		private static void DrawButton(SpriteBatch spriteBatch, Rectangle rectangle, string text)
		{
			bool hovered = rectangle.Contains(new Point(Main.mouseX, Main.mouseY));
			Color fill = hovered ? new Color(22, 25, 34) * 0.86f : new Color(10, 13, 20) * 0.74f;
			Color border = hovered ? new Color(176, 194, 206) * 0.86f : new Color(104, 118, 144) * 0.76f;
			DrawBox(spriteBatch, rectangle, fill, border);
			Vector2 size = FontAssets.MouseText.Value.MeasureString(text);
			float scale = Math.Min(0.86f, Math.Min((rectangle.Width - 16f) / Math.Max(1f, size.X), (rectangle.Height - 8f) / Math.Max(1f, size.Y)));
			ChatManager.DrawColorCodedStringWithShadow(spriteBatch, FontAssets.MouseText.Value, text, rectangle.Center.ToVector2(), Color.White, 0f, size * 0.5f, new Vector2(scale));
		}

		private Rectangle GetTextArea()
		{
			return GetTextArea(GetActiveInputRectangle());
		}

		private static Rectangle GetTextArea(Rectangle rectangle)
		{
			return new Rectangle(rectangle.X + 10, rectangle.Y + 10, rectangle.Width - 32, rectangle.Height - 20);
		}

		private Rectangle GetActiveInputRectangle()
		{
			return inputTarget switch
			{
				TextInputTarget.ProfileName => profileNameRectangle,
				TextInputTarget.ProfileDescription => profileDescriptionRectangle,
				_ => inputRectangle
			};
		}

		private float GetActiveInputTextScale()
		{
			return inputTarget == TextInputTarget.Chat ? 0.9f : 0.86f;
		}

		private int GetCursorIndexFromMouse(Point mouse)
		{
			Rectangle textArea = GetTextArea();
			float scale = GetActiveInputTextScale();
			List<EditableTextLine> lines = WrapEditableText(inputText ?? string.Empty, textArea, scale);
			if (lines.Count == 0)
			{
				return 0;
			}

			float lineHeight = FontAssets.MouseText.Value.LineSpacing * scale;
			int lineIndex = inputFirstVisibleLine + (int)((mouse.Y - textArea.Y) / lineHeight);
			lineIndex = Math.Min(Math.Max(lineIndex, 0), lines.Count - 1);
			EditableTextLine line = lines[lineIndex];
			if (string.IsNullOrEmpty(line.Text))
			{
				return line.StartIndex;
			}

			float relativeX = Math.Max(0f, mouse.X - textArea.X) / scale;
			return FindEditableIndexAtPosition(line, relativeX);
		}

		private static int FindEditableIndexAtPosition(EditableTextLine line, float horizontalPosition)
		{
			if (string.IsNullOrEmpty(line.Text))
			{
				return line.StartIndex;
			}

			int[] indexes = StringInfo.ParseCombiningCharacters(line.Text);
			for (int index = 0; index < indexes.Length; index++)
			{
				int elementStart = indexes[index];
				int elementEnd = index + 1 < indexes.Length ? indexes[index + 1] : line.Text.Length;
				float startWidth = elementStart <= 0 ? 0f : FontAssets.MouseText.Value.MeasureString(line.Text.Substring(0, elementStart)).X;
				float endWidth = FontAssets.MouseText.Value.MeasureString(line.Text.Substring(0, elementEnd)).X;
				if (horizontalPosition < (startWidth + endWidth) * 0.5f)
				{
					return line.StartIndex + elementStart;
				}
			}
			return line.EndIndex;
		}

		private void DrawEditableText(SpriteBatch spriteBatch, string text, Rectangle area, Color color, float scale)
		{
			List<EditableTextLine> lines = WrapEditableText(text ?? string.Empty, area, scale);
			float lineHeight = FontAssets.MouseText.Value.LineSpacing * scale;
			Texture2D pixel = TextureAssets.MagicPixel.Value;
			int selectionStart = GetSelectionStart();
			int selectionEnd = GetSelectionEnd();
			int caretLineIndex = FindEditableLineIndex(lines, inputCursorIndex);
			int visibleLineCount = Math.Max(1, (int)(area.Height / lineHeight));
			if (revealInputCursor)
			{
				if (caretLineIndex < inputFirstVisibleLine)
				{
					inputFirstVisibleLine = caretLineIndex;
				}
				else if (caretLineIndex >= inputFirstVisibleLine + visibleLineCount)
				{
					inputFirstVisibleLine = caretLineIndex - visibleLineCount + 1;
				}
			}
			inputFirstVisibleLine = Math.Min(Math.Max(inputFirstVisibleLine, 0), Math.Max(0, lines.Count - visibleLineCount));
			GraphicsDevice graphicsDevice = spriteBatch.GraphicsDevice;
			Rectangle oldScissorRectangle = graphicsDevice.ScissorRectangle;
			spriteBatch.End();
			graphicsDevice.ScissorRectangle = GetScaledScissorRectangle(area);
			spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, null, null, ScissorRasterizerState, null, Main.UIScaleMatrix);

			int lastVisibleLine = Math.Min(lines.Count, inputFirstVisibleLine + visibleLineCount);
			for (int index = inputFirstVisibleLine; index < lastVisibleLine; index++)
			{
				float y = area.Y + (index - inputFirstVisibleLine) * lineHeight;
				EditableTextLine line = lines[index];
				DrawEditableSelection(spriteBatch, pixel, line, area.X, y, lineHeight, scale, selectionStart, selectionEnd);
				if (!string.IsNullOrEmpty(line.Text))
				{
					Utils.DrawBorderString(spriteBatch, line.Text, new Vector2(area.X, y), color, scale);
				}

				if (ShouldDrawCaret() && index == caretLineIndex)
				{
					float caretX = area.X + MeasureEditablePrefix(line, inputCursorIndex) * scale;
					spriteBatch.Draw(pixel, new Rectangle((int)caretX, (int)y, 2, Math.Max(12, (int)lineHeight)), Color.White);
				}
			}

			spriteBatch.End();
			graphicsDevice.ScissorRectangle = oldScissorRectangle;
			spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, null, null, null, null, Main.UIScaleMatrix);
			revealInputCursor = false;
		}

		private void DrawEditableSelection(
			SpriteBatch spriteBatch,
			Texture2D pixel,
			EditableTextLine line,
			float x,
			float y,
			float lineHeight,
			float scale,
			int selectionStart,
			int selectionEnd)
		{
			int start = Math.Max(selectionStart, line.StartIndex);
			int end = Math.Min(selectionEnd, line.EndIndex);
			if (start >= end)
			{
				return;
			}

			float startX = x + MeasureEditablePrefix(line, start) * scale;
			float endX = x + MeasureEditablePrefix(line, end) * scale;
			int width = Math.Max(2, (int)(endX - startX));
			spriteBatch.Draw(pixel, new Rectangle((int)startX, (int)y, width, Math.Max(12, (int)lineHeight)), new Color(80, 120, 210) * 0.55f);
		}

		private static List<EditableTextLine> WrapEditableText(string text, Rectangle area, float scale)
		{
			List<EditableTextLine> lines = new();
			if (string.IsNullOrEmpty(text))
			{
				lines.Add(new EditableTextLine(string.Empty, 0, 0));
				return lines;
			}

			float maximumWidth = Math.Max(1f, area.Width / scale);
			int[] indexes = StringInfo.ParseCombiningCharacters(text);
			StringBuilder currentLine = new();
			int lineStart = 0;
			for (int index = 0; index < indexes.Length; index++)
			{
				int elementStart = indexes[index];
				int elementEnd = index + 1 < indexes.Length ? indexes[index + 1] : text.Length;
				string element = text.Substring(elementStart, elementEnd - elementStart);
				if (element == "\n")
				{
					lines.Add(new EditableTextLine(currentLine.ToString(), lineStart, elementStart));
					currentLine.Clear();
					lineStart = elementEnd;
					continue;
				}

				string proposed = currentLine + element;
				if (currentLine.Length > 0 && FontAssets.MouseText.Value.MeasureString(proposed).X > maximumWidth)
				{
					lines.Add(new EditableTextLine(currentLine.ToString(), lineStart, elementStart));
					currentLine.Clear();
					lineStart = elementStart;
				}
				currentLine.Append(element);
			}

			lines.Add(new EditableTextLine(currentLine.ToString(), lineStart, text.Length));
			return lines;
		}

		private static int FindEditableLineIndex(List<EditableTextLine> lines, int cursorIndex)
		{
			for (int index = 0; index < lines.Count; index++)
			{
				if (cursorIndex >= lines[index].StartIndex && cursorIndex <= lines[index].EndIndex)
				{
					return index;
				}
			}
			return Math.Max(0, lines.Count - 1);
		}

		private static float MeasureEditablePrefix(EditableTextLine line, int absoluteIndex)
		{
			int relativeIndex = Math.Min(Math.Max(absoluteIndex - line.StartIndex, 0), line.Text.Length);
			return relativeIndex <= 0 ? 0f : FontAssets.MouseText.Value.MeasureString(line.Text.Substring(0, relativeIndex)).X;
		}

		private static void DrawInsetInputBox(SpriteBatch spriteBatch, Rectangle rectangle, Color fill, Color rim, bool active)
		{
			Texture2D pixel = TextureAssets.MagicPixel.Value;
			spriteBatch.Draw(pixel, rectangle, fill);

			Color outerShadow = Color.Black * (active ? 0.72f : 0.56f);
			Color innerShadow = Color.Black * (active ? 0.42f : 0.3f);
			Color lowerRim = rim * (active ? 0.68f : 0.46f);
			Color sideRim = rim * (active ? 0.5f : 0.34f);
			spriteBatch.Draw(pixel, new Rectangle(rectangle.X, rectangle.Y, rectangle.Width, 1), outerShadow);
			spriteBatch.Draw(pixel, new Rectangle(rectangle.X, rectangle.Y, 1, rectangle.Height), outerShadow);
			spriteBatch.Draw(pixel, new Rectangle(rectangle.X + 1, rectangle.Y + 1, Math.Max(1, rectangle.Width - 2), 1), innerShadow);
			spriteBatch.Draw(pixel, new Rectangle(rectangle.X + 1, rectangle.Y + 1, 1, Math.Max(1, rectangle.Height - 2)), innerShadow);
			spriteBatch.Draw(pixel, new Rectangle(rectangle.X, rectangle.Bottom - 1, rectangle.Width, 1), lowerRim);
			spriteBatch.Draw(pixel, new Rectangle(rectangle.Right - 1, rectangle.Y, 1, rectangle.Height), sideRim);

			if (active)
			{
				spriteBatch.Draw(pixel, new Rectangle(rectangle.X + 2, rectangle.Bottom - 2, Math.Max(1, rectangle.Width - 4), 1), rim * 0.24f);
				spriteBatch.Draw(pixel, new Rectangle(rectangle.Right - 2, rectangle.Y + 2, 1, Math.Max(1, rectangle.Height - 4)), rim * 0.2f);
			}
		}

		private static void DrawBox(SpriteBatch spriteBatch, Rectangle rectangle, Color fill, Color border)
		{
			spriteBatch.Draw(TextureAssets.MagicPixel.Value, rectangle, fill);
			DrawBoxBorder(spriteBatch, rectangle, border);
		}

		private void DrawCircle(SpriteBatch spriteBatch, Rectangle rectangle, Color color)
		{
			smoothCircleTexture ??= CreateSmoothCircleTexture();
			spriteBatch.Draw(smoothCircleTexture, rectangle, color);
		}

		private static Texture2D CreateSmoothCircleTexture()
		{
			const int size = 128;
			Color[] pixels = new Color[size * size];
			float center = size * 0.5f;
			float radius = center - 1f;
			for (int y = 0; y < size; y++)
			{
				float offsetY = y + 0.5f - center;
				for (int x = 0; x < size; x++)
				{
					float offsetX = x + 0.5f - center;
					float distance = (float)Math.Sqrt(offsetX * offsetX + offsetY * offsetY);
					float coverage = MathHelper.Clamp(radius + 0.75f - distance, 0f, 1f);
					pixels[y * size + x] = Color.White * coverage;
				}
			}

			Texture2D texture = new(Main.graphics.GraphicsDevice, size, size);
			texture.SetData(pixels);
			return texture;
		}

		private static Texture2D CreateInverseCircleTexture()
		{
			const int size = 128;
			Color[] pixels = new Color[size * size];
			float center = size * 0.5f;
			float radius = center - 1.75f;
			for (int y = 0; y < size; y++)
			{
				float offsetY = y + 0.5f - center;
				for (int x = 0; x < size; x++)
				{
					float offsetX = x + 0.5f - center;
					float distance = (float)Math.Sqrt(offsetX * offsetX + offsetY * offsetY);
					float circleCoverage = MathHelper.Clamp(radius + 0.75f - distance, 0f, 1f);
					pixels[y * size + x] = Color.White * (1f - circleCoverage);
				}
			}

			Texture2D texture = new(Main.graphics.GraphicsDevice, size, size);
			texture.SetData(pixels);
			return texture;
		}

		private static void DrawBoxBorder(SpriteBatch spriteBatch, Rectangle rectangle, Color border)
		{
			Texture2D pixel = TextureAssets.MagicPixel.Value;
			spriteBatch.Draw(pixel, new Rectangle(rectangle.X, rectangle.Y, rectangle.Width, 1), Color.White * 0.18f);
			spriteBatch.Draw(pixel, new Rectangle(rectangle.X, rectangle.Y + 1, rectangle.Width, 1), border);
			spriteBatch.Draw(pixel, new Rectangle(rectangle.X, rectangle.Bottom - 2, rectangle.Width, 1), Color.Black * 0.28f);
			spriteBatch.Draw(pixel, new Rectangle(rectangle.X, rectangle.Bottom - 1, rectangle.Width, 1), border * 0.7f);
			spriteBatch.Draw(pixel, new Rectangle(rectangle.X, rectangle.Y, 1, rectangle.Height), border * 0.78f);
			spriteBatch.Draw(pixel, new Rectangle(rectangle.Right - 1, rectangle.Y, 1, rectangle.Height), border * 0.78f);
		}

		private Rectangle GetHistoryArea()
		{
			return new Rectangle(historyRectangle.X + 10, historyRectangle.Y + 10, historyRectangle.Width - 32, historyRectangle.Height - 20);
		}

		private Rectangle GetScrollHitRectangle()
		{
			Rectangle hitRectangle = scrollThumbRectangle == Rectangle.Empty
				? scrollTrackRectangle
				: Rectangle.Union(scrollTrackRectangle, scrollThumbRectangle);
			hitRectangle.Inflate(10, 5);
			return hitRectangle;
		}

		private Rectangle GetInputScrollHitRectangle()
		{
			Rectangle hitRectangle = inputScrollThumbRectangle == Rectangle.Empty
				? inputScrollTrackRectangle
				: Rectangle.Union(inputScrollTrackRectangle, inputScrollThumbRectangle);
			hitRectangle.Inflate(10, 5);
			return hitRectangle;
		}

		private static Rectangle GetScaledScissorRectangle(Rectangle area)
		{
			float scale = Main.UIScale;
			return new Rectangle((int)(area.X * scale), (int)(area.Y * scale), Math.Max(1, (int)(area.Width * scale)), Math.Max(1, (int)(area.Height * scale)));
		}

		private bool IsNewKeyPress(KeyboardState keyboardState, Keys key)
		{
			return keyboardState.IsKeyDown(key) && !lastCaptureKeyState.IsKeyDown(key);
		}

		private enum TextInputTarget
		{
			None,
			Chat,
			ProfileName,
			ProfileDescription
		}

		private readonly struct EditableTextLine
		{
			internal string Text { get; }
			internal int StartIndex { get; }
			internal int EndIndex { get; }

			internal EditableTextLine(string text, int startIndex, int endIndex)
			{
				Text = text;
				StartIndex = startIndex;
				EndIndex = endIndex;
			}
		}

		private readonly struct HistorySelectionPoint : IComparable<HistorySelectionPoint>, IEquatable<HistorySelectionPoint>
		{
			internal static HistorySelectionPoint Empty { get; } = new(-1, 0);
			internal int MessageIndex { get; }
			internal int CharacterIndex { get; }

			internal HistorySelectionPoint(int messageIndex, int characterIndex)
			{
				MessageIndex = messageIndex;
				CharacterIndex = Math.Max(0, characterIndex);
			}

			public int CompareTo(HistorySelectionPoint other)
			{
				int messageComparison = MessageIndex.CompareTo(other.MessageIndex);
				return messageComparison != 0 ? messageComparison : CharacterIndex.CompareTo(other.CharacterIndex);
			}

			public bool Equals(HistorySelectionPoint other)
			{
				return MessageIndex == other.MessageIndex && CharacterIndex == other.CharacterIndex;
			}
		}

		private readonly struct AvatarHitTarget
		{
			internal Rectangle Rectangle { get; }
			internal int PlayerId { get; }

			internal AvatarHitTarget(Rectangle rectangle, int playerId)
			{
				Rectangle = rectangle;
				PlayerId = playerId;
			}
		}

		private readonly struct MessageHitTarget
		{
			internal ChatEntry Message { get; }
			internal Rectangle BubbleRectangle { get; }
			internal Rectangle DeleteRectangle { get; }

			internal MessageHitTarget(ChatEntry message, Rectangle bubbleRectangle, Rectangle deleteRectangle)
			{
				Message = message;
				BubbleRectangle = bubbleRectangle;
				DeleteRectangle = deleteRectangle;
			}
		}

		private sealed class CachedMessageLayout
		{
			internal int MaximumBubbleWidth { get; }
			internal string[] Lines { get; }
			internal List<EditableTextLine> SelectableLines { get; }
			internal int LineCount { get; }
			internal int BubbleWidth { get; }
			internal int BubbleHeight { get; }

			internal CachedMessageLayout(int maximumBubbleWidth, string[] lines, List<EditableTextLine> selectableLines, int lineCount, int bubbleWidth, int bubbleHeight)
			{
				MaximumBubbleWidth = maximumBubbleWidth;
				Lines = lines;
				SelectableLines = selectableLines;
				LineCount = lineCount;
				BubbleWidth = bubbleWidth;
				BubbleHeight = bubbleHeight;
			}
		}

		private sealed class HistoryBubbleLayout
		{
			internal int MessageIndex { get; }
			internal ChatEntry Message { get; }
			internal int PlayerId { get; }
			internal string DisplayName { get; }
			internal Rectangle IconRectangle { get; }
			internal Rectangle BubbleRectangle { get; }
			internal Rectangle Bounds { get; }
			internal string[] Lines { get; }
			internal List<EditableTextLine> SelectableLines { get; }
			internal int LineCount { get; }

			internal HistoryBubbleLayout(int messageIndex, ChatEntry message, int playerId, string displayName, Rectangle iconRectangle, Rectangle bubbleRectangle, Rectangle bounds, string[] lines, List<EditableTextLine> selectableLines, int lineCount)
			{
				MessageIndex = messageIndex;
				Message = message;
				PlayerId = playerId;
				DisplayName = displayName;
				IconRectangle = iconRectangle;
				BubbleRectangle = bubbleRectangle;
				Bounds = bounds;
				Lines = lines;
				SelectableLines = selectableLines;
				LineCount = lineCount;
			}

			internal HistoryBubbleLayout Offset(float offset)
			{
				return new HistoryBubbleLayout(
					MessageIndex,
					Message,
					PlayerId,
					DisplayName,
					OffsetRectangle(IconRectangle, offset),
					OffsetRectangle(BubbleRectangle, offset),
					OffsetRectangle(Bounds, offset),
					Lines,
					SelectableLines,
					LineCount);
			}

			private static Rectangle OffsetRectangle(Rectangle rectangle, float offset)
			{
				return new Rectangle(rectangle.X, rectangle.Y - (int)offset, rectangle.Width, rectangle.Height);
			}
		}
	}
}
