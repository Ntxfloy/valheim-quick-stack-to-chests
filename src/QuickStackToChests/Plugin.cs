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
        public const string PluginVersion = "1.0.0";

        internal static ManualLogSource Log;

        internal static ConfigEntry<KeyboardShortcut> HotKey;
        internal static ConfigEntry<float> Radius;
        internal static ConfigEntry<bool> SkipFirstRow;
        internal static ConfigEntry<bool> SkipEquipped;
        internal static ConfigEntry<bool> IncludeNonStackable;
        internal static ConfigEntry<bool> FillEmptySlots;
        internal static ConfigEntry<bool> RespectWards;
        internal static ConfigEntry<bool> SkipContainersInUse;
        internal static ConfigEntry<string> ExcludedItems;
        internal static ConfigEntry<string> ExcludedContainers;
        internal static ConfigEntry<bool> ShowMessage;
        internal static ConfigEntry<bool> VerboseLog;

        private float _nextAllowedRun;

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
                "3. Предметы", "IncludeNonStackable", false,
                "Переносить и нестакающиеся предметы (оружие, броня, инструменты), если такие же лежат в сундуке.");

            FillEmptySlots = Config.Bind(
                "3. Предметы", "FillEmptySlots", true,
                "Если в сундуке уже есть такой предмет, но стаки заполнены - докладывать остаток в свободные слоты этого сундука.");

            ExcludedItems = Config.Bind(
                "3. Предметы", "ExcludedItems", "",
                "Предметы, которые никогда не переносить. Через запятую, префаб или имя: Wood, Coins, $item_coins");

            ShowMessage = Config.Bind(
                "4. Прочее", "ShowMessage", true,
                "Показывать сообщение о результате переноса на экране.");

            VerboseLog = Config.Bind(
                "4. Прочее", "VerboseLog", false,
                "Подробный лог в консоль BepInEx (для отладки).");

            Log.LogInfo($"{PluginName} v{PluginVersion} загружен. Клавиша: {HotKey.Value}");
        }

        private void Update()
        {
            if (Player.m_localPlayer == null || ZNetScene.instance == null)
            {
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
                Log.LogError($"Ошибка быстрого переноса: {e}");
            }
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
