using System;
using System.Collections;
using System.Collections.Generic;
using EFT.Interactive;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Manimal.Icebreaker.Keypad
{
    // one MonoBehaviour per active keypad UI session. instantiates the
    // KeypadCanvas prefab, wires button onClick + keyboard input, runs the
    // state machine (entering -> success/fail), plays audio cues, and unlocks
    // the bound door on success. all UI lookups are literal Transform.Find
    // paths from KeypadConstants — prefab reorganisation means updating there.
    internal sealed class KeypadSession : MonoBehaviour
    {
        private Keypad _keypad;

        // UI references resolved once at Begin time
        private GameObject  _uiInstance;
        private TMP_Text[]  _digitCells;
        private Image       _glowBorder;

        // the Display Image whose .sprite gets swapped between idle / success
        // / fail — sprites harvested once from disabled placeholder Images in
        // the prefab
        private Image  _displayImage;
        private Sprite _substrateIdle;
        private Sprite _substrateSuccess;
        private Sprite _substrateFail;

        // full-screen flash overlays under the KeypadCanvas root, authored at
        // alpha 0, pulsed on success/fail
        private Image _flashGreen;
        private Image _flashRed;

        // parsed once at Begin — RefreshDisplay runs every keypress
        private Color _colorEntered;
        private Color _colorPlaceholder;
        private readonly Dictionary<KeyCode, (Button button, char digit, ActionKind kind)> _keyBindings = new();

        // cached original Image color per button so key-up can restore it.
        // Button.colors are MULTIPLIERS, not absolute colors — writing
        // colors.normalColor (typically white) to the Image stomps the base
        // dark-grey chassis color and the button sticks white after release.
        private readonly Dictionary<Button, Color> _buttonBaseColors = new();

        private string _buffer = "";
        private SessionState _state = SessionState.Entering;

        // aliases — main row + numpad digits fire the same actions; Backspace
        // + Delete both clear; Return + KeypadEnter both submit
        private static readonly KeyCode[] DigitKeys =
        {
            KeyCode.Alpha0, KeyCode.Alpha1, KeyCode.Alpha2, KeyCode.Alpha3, KeyCode.Alpha4,
            KeyCode.Alpha5, KeyCode.Alpha6, KeyCode.Alpha7, KeyCode.Alpha8, KeyCode.Alpha9,
            KeyCode.Keypad0, KeyCode.Keypad1, KeyCode.Keypad2, KeyCode.Keypad3, KeyCode.Keypad4,
            KeyCode.Keypad5, KeyCode.Keypad6, KeyCode.Keypad7, KeyCode.Keypad8, KeyCode.Keypad9,
        };

        public void Begin(Keypad keypad)
        {
            if (keypad == null)
            {
                Plugin.Log?.LogError("[Keypad] session Begin called with null Keypad.");
                Destroy(gameObject);
                return;
            }

            _keypad = keypad;
            _keypad.ActiveSession = this;

            try
            {
                if (!SpawnUI())
                {
                    AbortSession("UI spawn failed");
                    return;
                }
                WireButtons();
                BuildKeyBindings();
                EnsureEventSystem();
                RefreshDisplay();
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"[Keypad] session Begin threw: {ex.GetType().Name}: {ex.Message}");
                AbortSession("exception in Begin");
            }
        }

        private bool SpawnUI()
        {
            // .Result is safe here — the driver awaited the bundle load at
            // raid start, so this returns the cached prefab instantly
            var (_, uiPrefab) = KeypadBundleLoader.EnsureLoaded().Result;
            if (uiPrefab == null)
            {
                Plugin.Log?.LogError("[Keypad] UI prefab not loaded.");
                return false;
            }

            _uiInstance = Instantiate(uiPrefab);
            _uiInstance.name = "KeypadCanvas (session)";
            _uiInstance.SetActive(true); // prefab ships disabled by default
            DontDestroyOnLoad(_uiInstance);

            _digitCells = ResolveDigitCells();
            if (_digitCells == null || _digitCells.Length == 0)
            {
                Plugin.Log?.LogError(
                    $"[Keypad] No digit cells resolved under '{KeypadConstants.UIPathDisplayCellsRoot}'. " +
                    $"Expected children named '{string.Format(KeypadConstants.CellNameFormat, 0)}'.." +
                    $"'{string.Format(KeypadConstants.CellNameFormat, KeypadConstants.DisplayLength - 1)}' " +
                    $"each containing a '{KeypadConstants.CellDigitChildName}' TMP_Text.");
                return false;
            }
            if (_digitCells.Length != KeypadConstants.DisplayLength)
            {
                Plugin.Log?.LogWarning(
                    $"[Keypad] resolved {_digitCells.Length} digit cells but DisplayLength is " +
                    $"{KeypadConstants.DisplayLength}. Display will be sized to the resolved count.");
            }

            if (!ColorUtility.TryParseHtmlString("#" + KeypadConstants.ColorEnteredHex, out _colorEntered))
                _colorEntered = Color.white;
            if (!ColorUtility.TryParseHtmlString("#" + KeypadConstants.ColorPlaceholderHex, out _colorPlaceholder))
                _colorPlaceholder = new Color(0.25f, 0.25f, 0.25f);

            var glowTf = _uiInstance.transform.Find(KeypadConstants.UIPathGlowBorder);
            if (glowTf != null)
                _glowBorder = glowTf.GetComponent<Image>();

            ResolveDisplaySubstrates();
            ResolveFlashImages();

            return true;
        }

        // grabs the two flash Images and force-resets both to alpha 0 —
        // defensive so a prefab tweak cant leak a stuck-open flash
        private void ResolveFlashImages()
        {
            _flashGreen = ResolveImage(KeypadConstants.UIPathFlashGreen);
            _flashRed   = ResolveImage(KeypadConstants.UIPathFlashRed);
            SetAlpha(_flashGreen, 0f);
            SetAlpha(_flashRed,   0f);
        }

        private Image ResolveImage(string path)
        {
            var tf = _uiInstance.transform.Find(path);
            if (tf == null)
            {
                Plugin.Log?.LogWarning($"[Keypad] flash image not found at '{path}'.");
                return null;
            }
            var img = tf.GetComponent<Image>();
            if (img == null)
                Plugin.Log?.LogWarning($"[Keypad] '{path}' has no Image component.");
            return img;
        }

        private static void SetAlpha(Image img, float a)
        {
            if (img == null) return;
            var c = img.color;
            c.a = a;
            img.color = c;
        }

        // three-phase pulse: fade 0 -> 1 over fadeIn, hold, fade 1 -> 0 over
        // fadeOut. zero/negative durations snap that phase.
        private IEnumerator FlashImage(Image img, float fadeIn, float hold, float fadeOut)
        {
            if (img == null) yield break;

            SetAlpha(img, 0f);

            if (fadeIn > 0f)
            {
                float t = 0f;
                while (t < fadeIn)
                {
                    t += Time.deltaTime;
                    SetAlpha(img, Mathf.Clamp01(t / fadeIn));
                    yield return null;
                }
            }
            SetAlpha(img, 1f);

            if (hold > 0f) yield return new WaitForSeconds(hold);

            if (fadeOut > 0f)
            {
                float t = 0f;
                while (t < fadeOut)
                {
                    t += Time.deltaTime;
                    SetAlpha(img, 1f - Mathf.Clamp01(t / fadeOut));
                    yield return null;
                }
            }
            SetAlpha(img, 0f);
        }

        // resolves the Display Image + Success/Fail substrate sprites off the
        // disabled SpriteHolders children. idle = whatever Display already has
        // authored in the prefab.
        private void ResolveDisplaySubstrates()
        {
            var displayTf = _uiInstance.transform.Find(KeypadConstants.UIPathDisplayParent);
            _displayImage = displayTf != null ? displayTf.GetComponent<Image>() : null;
            if (_displayImage == null)
            {
                Plugin.Log?.LogWarning(
                    $"[Keypad] No Image on '{KeypadConstants.UIPathDisplayParent}'; substrate swap disabled.");
                return;
            }

            _substrateIdle = _displayImage.sprite;

            var holders = _uiInstance.transform.Find(KeypadConstants.UIPathDisplaySpriteHolders);
            if (holders == null)
            {
                Plugin.Log?.LogWarning(
                    $"[Keypad] SpriteHolders not found at '{KeypadConstants.UIPathDisplaySpriteHolders}'; " +
                    "Success/Fail substrate swap disabled. Display stays gray.");
                return;
            }

            _substrateSuccess = HarvestSprite(holders, KeypadConstants.SubstrateSuccessChildName);
            _substrateFail    = HarvestSprite(holders, KeypadConstants.SubstrateFailChildName);
        }

        private Sprite HarvestSprite(Transform parent, string childName)
        {
            var child = parent.Find(childName);
            if (child == null)
            {
                Plugin.Log?.LogWarning($"[Keypad] SpriteHolders child '{childName}' not found.");
                return null;
            }
            var img = child.GetComponent<Image>();
            if (img == null || img.sprite == null)
            {
                Plugin.Log?.LogWarning(
                    $"[Keypad] SpriteHolders/{childName} has no Image.sprite to harvest.");
                return null;
            }
            return img.sprite;
        }

        private enum DisplayState { Idle, Success, Fail }

        private void SetDisplayState(DisplayState state)
        {
            if (_displayImage == null) return;
            Sprite next = state switch
            {
                DisplayState.Success => _substrateSuccess,
                DisplayState.Fail    => _substrateFail,
                _                    => _substrateIdle,
            };
            if (next != null) _displayImage.sprite = next;
        }

        // walks Cells/Cell_0/Digit ... Cells/Cell_N/Digit. stops at the first
        // missing cell so the array length reflects what the prefab actually
        // has; returns null only when the Cells root itself is missing.
        private TMP_Text[] ResolveDigitCells()
        {
            var cellsRoot = _uiInstance.transform.Find(KeypadConstants.UIPathDisplayCellsRoot);
            if (cellsRoot == null) return null;

            var list = new List<TMP_Text>(KeypadConstants.DisplayLength);
            for (int i = 0; i < KeypadConstants.DisplayLength; i++)
            {
                var cellName = string.Format(KeypadConstants.CellNameFormat, i);
                var cellTf = cellsRoot.Find(cellName);
                if (cellTf == null) break;
                var digitTf = cellTf.Find(KeypadConstants.CellDigitChildName);
                if (digitTf == null)
                {
                    Plugin.Log?.LogWarning(
                        $"[Keypad] Cell '{cellName}' has no child '{KeypadConstants.CellDigitChildName}'.");
                    break;
                }
                var tmp = digitTf.GetComponent<TMP_Text>();
                if (tmp == null)
                {
                    Plugin.Log?.LogWarning(
                        $"[Keypad] Cell '{cellName}/{KeypadConstants.CellDigitChildName}' has no TMP_Text.");
                    break;
                }
                list.Add(tmp);
            }
            return list.ToArray();
        }

        private void WireButtons()
        {
            var buttonsRoot = _uiInstance.transform.Find(KeypadConstants.UIPathButtonsRoot);
            if (buttonsRoot == null)
            {
                Plugin.Log?.LogError($"[Keypad] Buttons root not found at '{KeypadConstants.UIPathButtonsRoot}'.");
                return;
            }

            // hook the 10 digit buttons by child name — same handler whether
            // triggered by mouse click or keyboard
            for (int d = 0; d <= 9; d++)
            {
                var btn = FindButton(buttonsRoot, $"Btn_{d}");
                if (btn != null)
                {
                    var digit = (char)('0' + d);
                    btn.onClick.AddListener(() => OnDigit(digit));
                }
            }

            var clearBtn = FindButton(buttonsRoot, "Btn_Clear");
            if (clearBtn != null) clearBtn.onClick.AddListener(OnClear);

            var enterBtn = FindButton(buttonsRoot, "Btn_Enter");
            if (enterBtn != null) enterBtn.onClick.AddListener(OnEnter);

            // snapshot each button's Image color while it's still as-prefab so
            // key-up has something to restore to
            CacheButtonBaseColors(buttonsRoot);
        }

        private void CacheButtonBaseColors(Transform buttonsRoot)
        {
            foreach (var btn in buttonsRoot.GetComponentsInChildren<Button>(true))
            {
                if (btn == null) continue;
                var img = btn.targetGraphic as Image ?? btn.GetComponent<Image>();
                if (img == null) continue;
                _buttonBaseColors[btn] = img.color;
            }
        }

        // force-restores every cached button to its base color — used when the
        // Update loop is about to stop processing key-ups (state leaving
        // Entering) so no button gets stuck in pressed visual
        private void ResetAllButtonVisuals()
        {
            foreach (var kvp in _buttonBaseColors)
            {
                if (kvp.Key == null) continue;
                var img = kvp.Key.targetGraphic as Image ?? kvp.Key.GetComponent<Image>();
                if (img == null) continue;
                img.color = kvp.Value;
            }
        }

        private Button FindButton(Transform root, string childName)
        {
            var tf = root.Find(childName);
            return tf == null ? null : tf.GetComponent<Button>();
        }

        private void BuildKeyBindings()
        {
            var buttonsRoot = _uiInstance.transform.Find(KeypadConstants.UIPathButtonsRoot);
            if (buttonsRoot == null) return;

            foreach (var key in DigitKeys)
            {
                char digit = KeyToDigit(key);
                if (digit == '\0') continue;
                var btn = FindButton(buttonsRoot, $"Btn_{digit}");
                if (btn == null) continue;
                _keyBindings[key] = (btn, digit, ActionKind.Digit);
            }

            var clearBtn = FindButton(buttonsRoot, "Btn_Clear");
            if (clearBtn != null)
            {
                _keyBindings[KeyCode.Backspace] = (clearBtn, '\0', ActionKind.Clear);
                _keyBindings[KeyCode.Delete]    = (clearBtn, '\0', ActionKind.Clear);
            }

            var enterBtn = FindButton(buttonsRoot, "Btn_Enter");
            if (enterBtn != null)
            {
                _keyBindings[KeyCode.Return]      = (enterBtn, '\0', ActionKind.Enter);
                _keyBindings[KeyCode.KeypadEnter] = (enterBtn, '\0', ActionKind.Enter);
            }
        }

        private static char KeyToDigit(KeyCode key)
        {
            if (key >= KeyCode.Alpha0 && key <= KeyCode.Alpha9) return (char)('0' + (key - KeyCode.Alpha0));
            if (key >= KeyCode.Keypad0 && key <= KeyCode.Keypad9) return (char)('0' + (key - KeyCode.Keypad0));
            return '\0';
        }

        private void EnsureEventSystem()
        {
            // canvas buttons need an EventSystem to receive pointer events —
            // most EFT scenes ship one, bail-safe add if missing
            if (EventSystem.current == null)
            {
                var es = new GameObject("EventSystem");
                es.AddComponent<EventSystem>();
                es.AddComponent<StandaloneInputModule>();
                DontDestroyOnLoad(es);
            }
        }

        private void Update()
        {
            if (_state != SessionState.Entering) return;

            // keyboard input — on key DOWN fire the action and synthesize the
            // visual press on the matching button; on key UP release it
            foreach (var kvp in _keyBindings)
            {
                if (Input.GetKeyDown(kvp.Key))
                {
                    HandleAction(kvp.Value.kind, kvp.Value.digit);
                    SimulatePointer(kvp.Value.button, true);
                }
                if (Input.GetKeyUp(kvp.Key))
                {
                    SimulatePointer(kvp.Value.button, false);
                }
            }

            // F or ESC aborts without submitting — F matches vanilla EFT's
            // code-locked door UX, ESC kept as a safety fallback
            if (Input.GetKeyDown(KeyCode.F) || Input.GetKeyDown(KeyCode.Escape))
                AbortSession("cancel pressed");
        }

        // drives the visual press transition for keyboard input WITHOUT going
        // through the EventSystem — ExecuteEvents-based simulation fires
        // onClick as a side-effect, doubling the digit. Button.colors are
        // multipliers against the Image's base color: press multiplies the
        // cached base, release restores it verbatim.
        private void SimulatePointer(Button button, bool down)
        {
            if (button == null) return;
            try
            {
                var img = button.targetGraphic as Image ?? button.GetComponent<Image>();
                if (img == null) return;
                if (!_buttonBaseColors.TryGetValue(button, out var baseColor)) return;

                img.color = down ? baseColor * button.colors.pressedColor : baseColor;
            }
            catch { /* visual nicety — never break input on failure */ }
        }

        // --- action dispatch ----------------------------------------------

        private enum ActionKind { Digit, Clear, Enter }

        private void HandleAction(ActionKind kind, char digit)
        {
            switch (kind)
            {
                case ActionKind.Digit: OnDigit(digit); break;
                case ActionKind.Clear: OnClear();      break;
                case ActionKind.Enter: OnEnter();      break;
            }
        }

        private void OnDigit(char digit)
        {
            if (_state != SessionState.Entering) return;
            // cap at the display width, not the code length — the player can
            // fill the whole display and just has to press Enter when the
            // buffer matches
            if (_buffer.Length >= KeypadConstants.DisplayLength) return;
            _buffer += digit;
            PlayClip(_keypad.AudioKeypress);
            RefreshDisplay();
        }

        private void OnClear()
        {
            if (_state != SessionState.Entering) return;
            // backspace behavior: pop ONE digit — matches vanilla EFT's
            // code-locked door UX
            if (_buffer.Length == 0) return;
            _buffer = _buffer.Substring(0, _buffer.Length - 1);
            PlayClip(_keypad.AudioKeypress);
            RefreshDisplay();
        }

        private void OnEnter()
        {
            if (_state != SessionState.Entering) return;
            PlayClip(_keypad.AudioKeypress);

            // compare directly against the unlock code regardless of buffer
            // length — any non-match falls through to FailRoutine
            if (string.Equals(_buffer, _keypad.UnlockCode, StringComparison.Ordinal))
                StartCoroutine(SuccessRoutine());
            else
                StartCoroutine(FailRoutine());
        }

        // --- success / fail routines --------------------------------------

        private IEnumerator SuccessRoutine()
        {
            _state = SessionState.Succeeded;
            // Update bails when state != Entering, so a button held at success
            // time never sees its key-up — reset every button up front
            ResetAllButtonVisuals();
            PlayClip(_keypad.AudioGranted);
            SetDisplayColored("SUCCESS", KeypadConstants.ColorSuccessHex);
            SetDisplayState(DisplayState.Success);
            StartCoroutine(FlashImage(
                _flashGreen,
                KeypadConstants.FlashFadeInSeconds,
                KeypadConstants.FlashHoldSeconds,
                KeypadConstants.FlashFadeOutSeconds));

            // mark unlocked BEFORE the door flip so even if the door change
            // errors, the action patch still greys out further attempts
            _keypad.Unlocked = true;

            try
            {
                var door = _keypad.BoundDoor;
                if (door == null)
                {
                    Plugin.Log?.LogWarning("[Keypad] code accepted but no bound door — nothing to unlock.");
                }
                else
                {
                    // Locked -> Shut turns it into a regular openable door;
                    // already-open states are left alone
                    if (door.DoorState == EDoorState.Locked)
                        door.DoorState = EDoorState.Shut;
                    Plugin.Log?.LogInfo($"[Keypad] unlocked door '{door.Id}'.");
                }

                // spend the door's twin: shared-code peers within range disable
                // too (and their door unlocks — a no-op when its the same door).
                // a same-code terminal across the room stays live.
                float rangeSq = KeypadConstants.LinkedDisableRange * KeypadConstants.LinkedDisableRange;
                foreach (var twin in _keypad.LinkedKeypads)
                {
                    if (twin == null || twin.Unlocked) continue;
                    if ((twin.transform.position - _keypad.transform.position).sqrMagnitude > rangeSq) continue;
                    twin.Unlocked = true;
                    if (twin.BoundDoor != null && twin.BoundDoor.DoorState == EDoorState.Locked)
                        twin.BoundDoor.DoorState = EDoorState.Shut;
                    Plugin.Log?.LogInfo($"[Keypad] linked terminal '{twin.name}' disabled (within {KeypadConstants.LinkedDisableRange:F0}m).");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"[Keypad] door unlock threw: {ex.GetType().Name}: {ex.Message}");
            }

            yield return new WaitForSeconds(KeypadConstants.SuccessDisplaySeconds);
            EndSession();
        }

        private IEnumerator FailRoutine()
        {
            _state = SessionState.Failed;
            ResetAllButtonVisuals();
            PlayClip(_keypad.AudioDenied);
            SetDisplayColored("FAIL", KeypadConstants.ColorFailHex);
            SetDisplayState(DisplayState.Fail);
            StartCoroutine(FlashImage(
                _flashRed,
                KeypadConstants.FlashFadeInSeconds,
                KeypadConstants.FlashHoldSeconds,
                KeypadConstants.FlashFadeOutSeconds));

            yield return new WaitForSeconds(KeypadConstants.FailDisplaySeconds);

            // reset back to entering — retry without closing the UI
            _buffer = "";
            _state = SessionState.Entering;
            SetDisplayState(DisplayState.Idle);
            RefreshDisplay();
        }

        // --- display + visuals --------------------------------------------

        private void RefreshDisplay()
        {
            if (_digitCells == null) return;
            int n = _digitCells.Length;
            for (int i = 0; i < n; i++)
            {
                var cell = _digitCells[i];
                if (cell == null) continue;
                if (i < _buffer.Length)
                {
                    cell.text  = _buffer[i].ToString();
                    cell.color = _colorEntered;
                }
                else
                {
                    // placeholder "0" in dim color — dim zeros at rest, each
                    // replaced by the typed digit on press
                    cell.text  = "0";
                    cell.color = _colorPlaceholder;
                }
            }
        }

        // writes a status word ("SUCCESS" / "FAIL") across the digit cells,
        // centered — extra cells on either side are blanked
        private void SetDisplayColored(string text, string colorHex)
        {
            if (_digitCells == null) return;

            if (!ColorUtility.TryParseHtmlString("#" + colorHex, out var color))
                color = Color.white;

            int total   = _digitCells.Length;
            int len     = Mathf.Min(text?.Length ?? 0, total);
            int leftPad = (total - len) / 2;

            for (int i = 0; i < total; i++)
            {
                var cell = _digitCells[i];
                if (cell == null) continue;

                int textIdx = i - leftPad;
                if (textIdx >= 0 && textIdx < len)
                {
                    cell.text  = text[textIdx].ToString();
                    cell.color = color;
                }
                else
                {
                    cell.text = "";
                }
            }
        }

        private void PlayClip(AudioSource source)
        {
            if (source == null) return;
            try { source.Play(); } catch { /* never fatal */ }
        }

        // --- session teardown ---------------------------------------------

        private void EndSession()
        {
            try
            {
                if (_keypad != null && _keypad.ActiveSession == this)
                    _keypad.ActiveSession = null;
                if (_uiInstance != null) Destroy(_uiInstance);
            }
            finally
            {
                Destroy(gameObject);
            }
        }

        private void AbortSession(string reason)
        {
            Plugin.Log?.LogInfo($"[Keypad] session aborted: {reason}");
            EndSession();
        }

        private enum SessionState
        {
            Entering,
            Succeeded,
            Failed,
        }
    }
}
