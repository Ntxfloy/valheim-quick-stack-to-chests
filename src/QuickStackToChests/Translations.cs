using System;

namespace QuickStackToChests
{
    /// <summary>Uses Valheim's selected language at draw/message time. English is the safe fallback.</summary>
    internal static class Translations
    {
        internal static bool IsRussian
        {
            get
            {
                try
                {
                    return Localization.instance != null && string.Equals(
                        Localization.instance.GetSelectedLanguage(), "Russian", StringComparison.OrdinalIgnoreCase);
                }
                catch
                {
                    return false;
                }
            }
        }

        internal static string Text(string english, string russian)
        {
            return IsRussian ? russian : english;
        }

        internal static string NoNearbyChests => Text("No accessible chests nearby", "Рядом нет доступных сундуков");
        internal static string NothingToStack => Text("Nothing to stack", "Нечего раскладывать");
        internal static string Stacked(int items, int chests) => Text(
            $"Stacked {items} item(s) into {chests} chest(s)",
            $"Разложено {items} шт. по {chests} сундукам");
    }
}
