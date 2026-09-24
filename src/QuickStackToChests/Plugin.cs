using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace QuickStackToChests
{
    /// <summary>
    /// Quick Stack To Chests - переносит по горячей клавише все предметы из инвентаря игрока
    /// в ближайшие сундуки, где такие предметы уже лежат.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInProcess("valheim.exe")]
    public class QuickStackPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "blajion.quickstacktochests";
        public const string PluginName = "QuickStackToChests";
        public const string PluginVersion = "1.2.2";

        internal static ManualLogSource Log;

        internal static ConfigEntry<KeyboardShortcut> HotKey;
        internal static ConfigEntry<float> Radius;
        internal static ConfigEntry<bool> SkipFirstRow;
        internal static ConfigEntry<bool> SkipEquipped;
        internal static ConfigEntry<bool> IncludeNonStackable;
        internal static ConfigEntry<bool> FillEmptySlots;
        internal static ConfigEntry<bool> StrictWorldLevelMatch;
        internal static ConfigEntry<bool> RespectWards;
        internal static ConfigEntry<bool> SkipContainersInUse;
        internal static ConfigEntry<string> ExcludedItems;
        internal static ConfigEntry<string> ExcludedContainers;
        internal static ConfigEntry<bool> ShowMessage;
        internal static ConfigEntry<bool> Diagnostics;
        internal static ConfigEntry<bool> VerboseLog;

        private float _nextAllowedRun;
        private bool _settingsOpen;
        private bool _waitingForHotKey;
        private Rect _settingsRect = new Rect(30f, 100f, 460f, 420f);

        private void Awake()
        {
            Log = Logger;

            HotKey = Config.Bind(
                "1. Управление", "HotKey",
                new KeyboardShortcut(KeyCode.Q, KeyCode.LeftControl),
                "Горячая клавиша переноса. Формат BepInEx: 'Q + LeftControl', 'V', 'S + LeftAlt' и т.п.");

            Radius = Config.Bind(
                "2. Поиск сундуков", "Radius", 12f,
                new ConfigDescription("Радиус поиска сундуков вокруг игрока (метры).",
                    new AcceptableValueRange<float>(1f, 60f)));

            SkipContainersInUse = Config.Bind(
                "2. Поиск сундуков", "SkipContainersInUse", true,
                "Пропускать сундуки, которые сейчас открыты кем-то (в том числе вами).");

            RespectWards = Config.Bind(
                "2. Поиск сундуков", "RespectWards", true,
                "Учитывать ворды (Ward) и приватные сундуки: не трогать чужое.");

            ExcludedContainers = Config.Bind(
                "2. Поиск сундуков", "ExcludedContainers", "",
                "Префабы сундуков, которые игнорировать. Через запятую, например: piece_chest_private, Container_wood");

            SkipFirstRow = Config.Bind(
                "3. Предметы", "SkipFirstRow", true,
                "Не трогать первый ряд инвентаря (хотбар) - там обычно инструменты и еда.");

            SkipEquipped = Config.Bind(
                "3. Предметы", "SkipEquipped", true,
                "Не трогать экипированные предметы.");

            IncludeNonStackable = Config.Bind(
                "3. Предметы", "IncludeNonStackable", true,
                "Переносить и нестакающиеся предметы (оружие, броня, инструменты), если такие же лежат в сундуке.");

            FillEmptySlots = Config.Bind(
                "3. Предметы", "FillEmptySlots", true,
                "Если в сундуке уже есть такой предмет, но стаки заполнены - докладывать остаток в свободные слоты этого сундука.");

            StrictWorldLevelMatch = Config.Bind(
                "3. Предметы", "StrictWorldLevelMatch", false,
                "Строго сравнивать m_worldLevel предметов. Включать только если мод объединяет предметы разных уровней мира, которые в игре не стакаются.");

            ExcludedItems = Config.Bind(
                "3. Предметы", "ExcludedItems", "",
                "Предметы, которые никогда не переносить. Через запятую, префаб или имя: Wood, Coins, $item_coins");

            ShowMessage = Config.Bind(
                "4. Прочее", "ShowMessage", true,
                "Показывать сообщение о результате переноса на экране.");

            Diagnostics = Config.Bind(
                "4. Прочее", "Diagnostics", true,
                "Писать в лог итог каждого нажатия: сколько найдено сундуков, сколько перенесено и почему предметы пропущены.");

            VerboseLog = Config.Bind(
                "4. Прочее", "VerboseLog", false,
                "Подробный лог каждого переноса по предметам и сундукам (шумно).");

            Log.LogInfo($"{PluginName} v{PluginVersion} loaded. Hotkey: {HotKey.Value}");
        }

        private void Update()
        {
            if (Player.m_localPlayer == null || ZNetScene.instance == null)
            {
                return;
            }

            if (Input.GetKeyDown(KeyCode.F8) && !Console.IsVisible() && !TextInput.IsVisible())
            {
                _settingsOpen = !_settingsOpen;
                _waitingForHotKey = false;
            }

            if (_settingsOpen)
            {
                if (_waitingForHotKey)
                {
                    TryReadSideMouseBinding();
                }

                return;
            }

            if (Time.time < _nextAllowedRun)
            {
                return;
            }

            if (IsTypingOrBlocked())
            {
                return;
            }

            if (!HotKey.Value.IsDown())
            {
                return;
            }

            _nextAllowedRun = Time.time + 0.3f;

            try
            {
                QuickStack.Run();
            }
            catch (System.Exception e)
            {
                Log.LogError($"Quick stack error: {e}");
            }
        }

        /// <summary>Небольшая встроенная панель без зависимости от сторонних модов настроек.</summary>
        private void OnGUI()
        {
            if (!_settingsOpen)
            {
                return;
            }

            _settingsRect = GUI.Window(731942, _settingsRect, DrawSettingsWindow,
                Translations.Text("QuickStackToChests — Settings", "QuickStackToChests — Настройки"));
        }

        private void DrawSettingsWindow(int windowId)
        {
            GUILayout.BeginVertical();
            GUILayout.Label(Translations.Text("F8 — close. Changes save immediately.", "F8 — закрыть. Настройки сохраняются сразу."));
            GUILayout.Space(8f);

            GUILayout.BeginHorizontal();
            GUILayout.Label(Translations.Text("Quick stack hotkey:", "Быстрая сортировка:"), GUILayout.Width(150f));
            string buttonText = _waitingForHotKey
                ? Translations.Text("Press a key or side mouse button… (Esc — cancel)", "Нажми клавишу или боковую кнопку мыши… (Esc — отмена)")
                : HotKey.Value.ToString();
            if (GUILayout.Button(buttonText, GUILayout.Width(260f)))
            {
                _waitingForHotKey = true;
            }
            GUILayout.EndHorizontal();

            if (_waitingForHotKey)
            {
                ReadNewHotKey(Event.current);
            }

            GUILayout.Space(8f);
            GUILayout.BeginHorizontal();
            GUILayout.Label(Translations.Text($"Chest radius: {Radius.Value:0} m", $"Радиус сундуков: {Radius.Value:0} м"), GUILayout.Width(180f));
            float newRadius = GUILayout.HorizontalSlider(Radius.Value, 1f, 60f, GUILayout.Width(220f));
            float roundedRadius = Mathf.Round(newRadius);
            if (!Mathf.Approximately(roundedRadius, Radius.Value))
            {
                Radius.Value = roundedRadius;
                Config.Save();
            }
            GUILayout.EndHorizontal();

            DrawToggle(Translations.Text("Skip first inventory row", "Не трогать первый ряд инвентаря"), SkipFirstRow);
            DrawToggle(Translations.Text("Skip equipped items", "Не трогать экипированные вещи"), SkipEquipped);
            DrawToggle(Translations.Text("Respect wards and private chests", "Уважать ворды и приватные сундуки"), RespectWards);
            DrawToggle(Translations.Text("Skip chests currently in use", "Пропускать открытые сундуки"), SkipContainersInUse);
            DrawToggle(Translations.Text("Fill empty slots in matching chests", "Класть остаток в свободные ячейки сундука"), FillEmptySlots);
            DrawToggle(Translations.Text("Include weapons, armor and tools", "Переносить оружие, броню и инструменты"), IncludeNonStackable);
            DrawToggle(Translations.Text("Strict world-level match", "Строгое сравнение уровня мира"), StrictWorldLevelMatch);
            DrawToggle(Translations.Text("Log diagnostics", "Писать диагностику в лог"), Diagnostics);

            GUILayout.FlexibleSpace();
            if (GUILayout.Button(Translations.Text("Close (F8)", "Закрыть (F8)"), GUILayout.Height(28f)))
            {
                _settingsOpen = false;
                _waitingForHotKey = false;
            }
            GUILayout.EndVertical();
            GUI.DragWindow(new Rect(0f, 0f, 10000f, 24f));
        }

        private void DrawToggle(string label, ConfigEntry<bool> setting)
        {
            bool value = GUILayout.Toggle(setting.Value, label);
            if (value != setting.Value)
            {
                setting.Value = value;
                Config.Save();
            }
        }

        private void ReadNewHotKey(Event currentEvent)
        {
            if (currentEvent == null || currentEvent.type != EventType.KeyDown)
            {
                return;
            }

            if (currentEvent.keyCode == KeyCode.Escape)
            {
                _waitingForHotKey = false;
                currentEvent.Use();
                return;
            }

            if (IsModifierKey(currentEvent.keyCode))
            {
                return;
            }

            var modifiers = new System.Collections.Generic.List<KeyCode>();
            if (currentEvent.control) modifiers.Add(KeyCode.LeftControl);
            if (currentEvent.shift) modifiers.Add(KeyCode.LeftShift);
            if (currentEvent.alt) modifiers.Add(KeyCode.LeftAlt);
            if (currentEvent.command) modifiers.Add(KeyCode.LeftCommand);

            HotKey.Value = new KeyboardShortcut(currentEvent.keyCode, modifiers.ToArray());
            Config.Save();
            Log.LogInfo($"New quick stack hotkey: {HotKey.Value}");
            _waitingForHotKey = false;
            currentEvent.Use();
        }

        /// <summary>ЛКМ, ПКМ и колёсико намеренно не поддерживаем: только боковые кнопки.</summary>
        private void TryReadSideMouseBinding()
        {
            if (Input.GetMouseButtonDown(3))
            {
                SetMouseHotKey(KeyCode.Mouse3);
            }
            else if (Input.GetMouseButtonDown(4))
            {
                SetMouseHotKey(KeyCode.Mouse4);
            }
            else if (Input.GetMouseButtonDown(5))
            {
                SetMouseHotKey(KeyCode.Mouse5);
            }
        }

        private void SetMouseHotKey(KeyCode mouseButton)
        {
            HotKey.Value = new KeyboardShortcut(mouseButton);
            Config.Save();
            Log.LogInfo($"New quick stack hotkey: {HotKey.Value}");
            _waitingForHotKey = false;
        }

        private static bool IsModifierKey(KeyCode key)
        {
            return key == KeyCode.LeftControl || key == KeyCode.RightControl ||
                   key == KeyCode.LeftShift || key == KeyCode.RightShift ||
                   key == KeyCode.LeftAlt || key == KeyCode.RightAlt ||
                   key == KeyCode.LeftCommand || key == KeyCode.RightCommand;
        }

        /// <summary>Не срабатывать, когда игрок печатает в чате/консоли или сидит в меню.</summary>
        private static bool IsTypingOrBlocked()
        {
            if (Console.IsVisible())
            {
                return true;
            }

            if (TextInput.IsVisible())
            {
                return true;
            }

            if (Menu.IsVisible())
            {
                return true;
            }

            if (IsChatFocused())
            {
                return true;
            }

            return false;
        }

        // Сигнатура фокуса чата менялась между версиями игры - берём через рефлексию.
        private static System.Reflection.MethodInfo _chatHasFocus;
        private static bool _chatHasFocusResolved;

        private static bool IsChatFocused()
        {
            if (Chat.instance == null)
            {
                return false;
            }

            if (!_chatHasFocusResolved)
            {
                _chatHasFocusResolved = true;
                _chatHasFocus = typeof(Chat).GetMethod(
                    "HasFocus",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.FlattenHierarchy,
                    null, System.Type.EmptyTypes, null);
            }

            if (_chatHasFocus == null)
            {
                return false;
            }

            try
            {
                return _chatHasFocus.Invoke(Chat.instance, null) is bool focused && focused;
            }
            catch
            {
                return false;
            }
        }
    }
}
