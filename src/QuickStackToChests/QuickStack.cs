using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace QuickStackToChests
{
    /// <summary>
    /// Вся логика переноса. Осознанно написано на публичном API Valheim + рефлексии там,
    /// где сигнатуры менялись между патчами игры, чтобы мод не ломался после обновлений.
    /// </summary>
    internal static class QuickStack
    {
        private const BindingFlags AllInstance =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private const BindingFlags AllStatic =
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        private static readonly MethodInfo ContainerSaveMethod =
            typeof(Container).GetMethod("Save", AllInstance, null, Type.EmptyTypes, null);

        private static readonly MethodInfo ContainerLoadMethod =
            typeof(Container).GetMethod("Load", AllInstance, null, Type.EmptyTypes, null);

        private static readonly MethodInfo MoveItemToThisMethod =
            typeof(Inventory).GetMethod(
                "MoveItemToThis", AllInstance, null,
                new[] { typeof(Inventory), typeof(ItemDrop.ItemData), typeof(int), typeof(int), typeof(int) },
                null);

        // В Valheim 1.0 публичный Inventory.Changed() заменён на приватный Changed(bool, bool).
        private static readonly MethodInfo InventoryChangedNoArgs =
            typeof(Inventory).GetMethod("Changed", AllInstance, null, Type.EmptyTypes, null);

        private static readonly MethodInfo InventoryChangedTwoArgs =
            typeof(Inventory).GetMethod(
                "Changed", AllInstance, null, new[] { typeof(bool), typeof(bool) }, null);

        // Сигнатура PrivateArea.CheckAccess тоже менялась (3 и 4 аргумента).
        private static readonly MethodInfo PrivateAreaCheckAccess4 =
            typeof(PrivateArea).GetMethod(
                "CheckAccess", AllStatic, null,
                new[] { typeof(Vector3), typeof(float), typeof(bool), typeof(bool) }, null);

        private static readonly MethodInfo PrivateAreaCheckAccess3 =
            typeof(PrivateArea).GetMethod(
                "CheckAccess", AllStatic, null,
                new[] { typeof(Vector3), typeof(float), typeof(bool) }, null);

        private static readonly MethodInfo InCutsceneMethod =
            typeof(Character).GetMethod("InCutscene", AllInstance, null, Type.EmptyTypes, null);

        private static readonly FieldInfo WorldLevelField =
            typeof(ItemDrop.ItemData).GetField("m_worldLevel", AllInstance);

        private static int _layerMask;
        private static readonly Collider[] HitBuffer = new Collider[512];

        /// <summary>Счётчики причин, почему предмет не ушёл в сундук - без них отладка слепая.</summary>
        private sealed class Stats
        {
            internal int ContainersFound;
            internal int ContainersInUse;
            internal int ContainersNoAccess;
            internal int SkippedFirstRow;
            internal int SkippedEquipped;
            internal int SkippedNonStackable;
            internal int SkippedExcluded;
            internal int SkippedQuest;
            internal int NoMatchInChests;
            internal int MatchedButFull;
            internal int MoveFailed;
            internal readonly HashSet<string> NoMatchNames = new HashSet<string>();
            internal readonly HashSet<string> FullNames = new HashSet<string>();
            internal readonly HashSet<string> FailedNames = new HashSet<string>();
        }

        internal static void Run()
        {
            Player player = Player.m_localPlayer;
            if (player == null || player.IsDead() || IsInCutscene(player))
            {
                return;
            }

            Inventory playerInventory = player.GetInventory();
            if (playerInventory == null)
            {
                return;
            }

            var stats = new Stats();
            HashSet<string> excludedItems = ParseList(QuickStackPlugin.ExcludedItems.Value);
            HashSet<string> excludedContainers = ParseList(QuickStackPlugin.ExcludedContainers.Value);

            List<Container> containers = FindNearbyContainers(player, excludedContainers, stats);
            if (containers.Count == 0)
            {
                string why = stats.ContainersInUse > 0 || stats.ContainersNoAccess > 0
                    ? $"Сундуки недоступны (открыты: {stats.ContainersInUse}, нет доступа: {stats.ContainersNoAccess})"
                    : "Рядом нет доступных сундуков";
                Message(player, why);
                LogSummary(stats, 0, 0);
                return;
            }

            int movedItems = 0;
            int usedContainers = 0;

            // Отбираем предметы-кандидаты один раз, с учётом фильтров и со статистикой причин.
            List<ItemDrop.ItemData> candidates = playerInventory.GetAllItems()
                .Where(item => IsTransferable(item, excludedItems, stats))
                .ToList();

            foreach (Container container in containers)
            {
                Inventory containerInventory = PrepareContainer(container);
                if (containerInventory == null)
                {
                    continue;
                }

                int movedHere = 0;

                foreach (ItemDrop.ItemData item in candidates)
                {
                    if (item.m_stack <= 0 || !playerInventory.ContainsItem(item))
                    {
                        continue;
                    }

                    movedHere += StackItemIntoContainer(playerInventory, containerInventory, item, stats, container);
                }

                if (movedHere > 0)
                {
                    movedItems += movedHere;
                    usedContainers++;
                    FinishContainer(container);

                    if (QuickStackPlugin.VerboseLog.Value)
                    {
                        QuickStackPlugin.Log.LogInfo(
                            $"Перенесено {movedHere} шт. в {container.name} ({container.transform.position})");
                    }
                }
            }

            if (movedItems > 0)
            {
                NotifyChanged(playerInventory);
                Message(player, $"Разложено {movedItems} шт. по {usedContainers} сундукам");
            }
            else
            {
                Message(player, BuildNothingMessage(stats, candidates.Count, containers.Count));
            }

            LogSummary(stats, movedItems, usedContainers);
        }

        private static string BuildNothingMessage(Stats stats, int candidateCount, int containerCount)
        {
            if (candidateCount == 0)
            {
                var skipped = new List<string>();
                if (stats.SkippedFirstRow > 0) skipped.Add($"1-й ряд: {stats.SkippedFirstRow}");
                if (stats.SkippedEquipped > 0) skipped.Add($"надето: {stats.SkippedEquipped}");
                if (stats.SkippedNonStackable > 0) skipped.Add($"не стакается: {stats.SkippedNonStackable}");
                if (stats.SkippedExcluded > 0) skipped.Add($"исключено: {stats.SkippedExcluded}");

                return skipped.Count > 0
                    ? "Всё отфильтровано (" + string.Join(", ", skipped.ToArray()) + ")"
                    : "Инвентарь пуст";
            }

            if (stats.MoveFailed > 0)
            {
                return $"Не удалось перенести ({stats.MoveFailed} попыток) - см. лог";
            }

            if (stats.MatchedButFull > 0)
            {
                return "Совпадения есть, но сундуки забиты";
            }

            return $"Нет совпадений: проверено {candidateCount} предметов в {containerCount} сундуках";
        }

        private static void LogSummary(Stats stats, int moved, int usedContainers)
        {
            if (!QuickStackPlugin.Diagnostics.Value)
            {
                return;
            }

            QuickStackPlugin.Log.LogInfo(
                $"[quickstack] сундуков: {stats.ContainersFound} (открыты: {stats.ContainersInUse}, без доступа: {stats.ContainersNoAccess}); " +
                $"перенесено: {moved} шт. в {usedContainers}; " +
                $"пропущено [1й ряд: {stats.SkippedFirstRow}, надето: {stats.SkippedEquipped}, не стак: {stats.SkippedNonStackable}, " +
                $"искл: {stats.SkippedExcluded}, квест: {stats.SkippedQuest}]; " +
                $"без совпадения: {stats.NoMatchInChests}, сундук полный: {stats.MatchedButFull}, ошибка переноса: {stats.MoveFailed}");

            if (stats.NoMatchNames.Count > 0)
            {
                QuickStackPlugin.Log.LogInfo("[quickstack] нет таких в сундуках: " + string.Join(", ", stats.NoMatchNames.ToArray()));
            }

            if (stats.FullNames.Count > 0)
            {
                QuickStackPlugin.Log.LogInfo("[quickstack] места нет для: " + string.Join(", ", stats.FullNames.ToArray()));
            }

            if (stats.FailedNames.Count > 0)
            {
                QuickStackPlugin.Log.LogWarning("[quickstack] перенос не удался для: " + string.Join(", ", stats.FailedNames.ToArray()));
            }
        }

        // ---------------------------------------------------------------- поиск сундуков

        private static List<Container> FindNearbyContainers(Player player, HashSet<string> excluded, Stats stats)
        {
            var result = new List<Container>();
            var seen = new HashSet<Container>();

            Vector3 center = player.transform.position;
            float radius = QuickStackPlugin.Radius.Value;
            long playerId = player.GetPlayerID();

            int count = Physics.OverlapSphereNonAlloc(center, radius, HitBuffer, GetLayerMask());

            for (int i = 0; i < count; i++)
            {
                Collider collider = HitBuffer[i];
                if (collider == null)
                {
                    continue;
                }

                Container container = collider.GetComponentInParent<Container>();
                if (container == null || !seen.Add(container))
                {
                    continue;
                }

                if (!IsUsableContainer(container, playerId, excluded, stats))
                {
                    continue;
                }

                result.Add(container);
            }

            stats.ContainersFound = result.Count;

            result.Sort((a, b) =>
                Vector3.SqrMagnitude(a.transform.position - center)
                    .CompareTo(Vector3.SqrMagnitude(b.transform.position - center)));

            return result;
        }

        private static bool IsUsableContainer(Container container, long playerId, HashSet<string> excluded, Stats stats)
        {
            ZNetView nview = container.m_nview;
            if (nview == null || !nview.IsValid())
            {
                return false;
            }

            // Инвентарь игрока/трупа/другого персонажа - не наш случай.
            if (container.GetComponentInParent<Player>() != null)
            {
                return false;
            }

            if (container.GetInventory() == null)
            {
                return false;
            }

            if (QuickStackPlugin.SkipContainersInUse.Value && container.IsInUse())
            {
                stats.ContainersInUse++;
                return false;
            }

            // Приватный сундук другого игрока.
            if (!container.CheckAccess(playerId))
            {
                stats.ContainersNoAccess++;
                return false;
            }

            if (QuickStackPlugin.RespectWards.Value && !CheckWardAccess(container.transform.position))
            {
                stats.ContainersNoAccess++;
                return false;
            }

            if (excluded.Count > 0)
            {
                string prefab = Utils.GetPrefabName(container.gameObject);
                if (excluded.Contains(Normalize(prefab)) || excluded.Contains(Normalize(container.m_name)))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>Проверка защитного круга с учётом разных сигнатур между версиями игры.</summary>
        private static bool CheckWardAccess(Vector3 position)
        {
            try
            {
                if (PrivateAreaCheckAccess4 != null)
                {
                    return (bool)PrivateAreaCheckAccess4.Invoke(null, new object[] { position, 0f, false, false });
                }

                if (PrivateAreaCheckAccess3 != null)
                {
                    return (bool)PrivateAreaCheckAccess3.Invoke(null, new object[] { position, 0f, false });
                }
            }
            catch (Exception e)
            {
                QuickStackPlugin.Log.LogWarning($"PrivateArea.CheckAccess недоступен: {e.Message}");
            }

            return true;
        }

        private static bool IsInCutscene(Player player)
        {
            if (InCutsceneMethod == null)
            {
                return false;
            }

            try
            {
                return (bool)InCutsceneMethod.Invoke(player, null);
            }
            catch
            {
                return false;
            }
        }

        private static int GetLayerMask()
        {
            if (_layerMask != 0)
            {
                return _layerMask;
            }

            string[] names =
            {
                "Default", "static_solid", "Default_small", "piece", "piece_nonsolid", "vehicle", "item"
            };

            int mask = 0;
            foreach (string name in names)
            {
                int layer = LayerMask.NameToLayer(name);
                if (layer >= 0)
                {
                    mask |= 1 << layer;
                }
            }

            _layerMask = mask != 0 ? mask : ~0;
            return _layerMask;
        }

        // ------------------------------------------------- синхронизация сундука в сети

        private static Inventory PrepareContainer(Container container)
        {
            ZNetView nview = container.m_nview;
            if (nview == null || !nview.IsValid())
            {
                return null;
            }

            if (!nview.IsOwner())
            {
                nview.ClaimOwnership();
                Invoke(ContainerLoadMethod, container, "Container.Load");
            }

            return container.GetInventory();
        }

        private static void FinishContainer(Container container)
        {
            Inventory inventory = container.GetInventory();
            if (inventory != null)
            {
                NotifyChanged(inventory);
            }

            Invoke(ContainerSaveMethod, container, "Container.Save");
        }

        /// <summary>
        /// Inventory.Changed(): до 1.0 - публичный без аргументов, в 1.0 - приватный Changed(bool, bool).
        /// </summary>
        private static void NotifyChanged(Inventory inventory)
        {
            if (inventory == null)
            {
                return;
            }

            try
            {
                if (InventoryChangedNoArgs != null)
                {
                    InventoryChangedNoArgs.Invoke(inventory, null);
                    return;
                }

                if (InventoryChangedTwoArgs != null)
                {
                    InventoryChangedTwoArgs.Invoke(inventory, new object[] { false, false });
                }
            }
            catch (Exception e)
            {
                QuickStackPlugin.Log.LogWarning($"Inventory.Changed завершился ошибкой: {e.Message}");
            }
        }

        private static void Invoke(MethodInfo method, Container container, string label)
        {
            if (method == null)
            {
                return;
            }

            try
            {
                method.Invoke(container, null);
            }
            catch (Exception e)
            {
                QuickStackPlugin.Log.LogWarning($"{label} завершился ошибкой: {e.Message}");
            }
        }

        // ------------------------------------------------------------- отбор предметов

        private static bool IsTransferable(ItemDrop.ItemData item, HashSet<string> excluded, Stats stats)
        {
            if (item == null || item.m_shared == null || item.m_stack <= 0)
            {
                return false;
            }

            if (QuickStackPlugin.SkipEquipped.Value && item.m_equipped)
            {
                stats.SkippedEquipped++;
                return false;
            }

            // Первый ряд инвентаря = хотбар.
            if (QuickStackPlugin.SkipFirstRow.Value && item.m_gridPos.y == 0)
            {
                stats.SkippedFirstRow++;
                return false;
            }

            if (item.m_shared.m_questItem)
            {
                stats.SkippedQuest++;
                return false;
            }

            if (!QuickStackPlugin.IncludeNonStackable.Value && item.m_shared.m_maxStackSize <= 1)
            {
                stats.SkippedNonStackable++;
                return false;
            }

            if (excluded.Count > 0)
            {
                string prefab = item.m_dropPrefab != null ? item.m_dropPrefab.name : string.Empty;
                if (excluded.Contains(Normalize(prefab)) ||
                    excluded.Contains(Normalize(item.m_shared.m_name)) ||
                    excluded.Contains(Normalize(Localization.instance.Localize(item.m_shared.m_name))))
                {
                    stats.SkippedExcluded++;
                    return false;
                }
            }

            return true;
        }

        private static bool IsSameItem(ItemDrop.ItemData a, ItemDrop.ItemData b)
        {
            if (a == null || b == null || a.m_shared == null || b.m_shared == null)
            {
                return false;
            }

            if (ReferenceEquals(a, b))
            {
                return false;
            }

            if (a.m_shared.m_name != b.m_shared.m_name)
            {
                return false;
            }

            if (a.m_quality != b.m_quality || a.m_variant != b.m_variant)
            {
                return false;
            }

            // m_worldLevel строго сравниваем только по желанию: в 1.0 это поле часто различается
            // у одинаковых на вид предметов и раньше ломало совпадения.
            if (QuickStackPlugin.StrictWorldLevelMatch.Value && WorldLevelField != null)
            {
                object left = WorldLevelField.GetValue(a);
                object right = WorldLevelField.GetValue(b);
                if (!Equals(left, right))
                {
                    return false;
                }
            }

            return true;
        }

        // ---------------------------------------------------------------- сам перенос

        /// <summary>
        /// Кладёт предмет в сундук ТОЛЬКО если такой же предмет там уже лежит.
        /// Возвращает количество перенесённых штук.
        /// </summary>
        private static int StackItemIntoContainer(
            Inventory from, Inventory to, ItemDrop.ItemData item, Stats stats, Container container)
        {
            if (!to.GetAllItems().Any(existing => IsSameItem(existing, item)))
            {
                stats.NoMatchInChests++;
                stats.NoMatchNames.Add(ItemLabel(item));
                return 0;
            }

            int moved = 0;
            bool anyAttempt = false;

            // 1. Сначала добиваем неполные стаки.
            var partials = to.GetAllItems()
                .Where(existing => IsSameItem(existing, item) && existing.m_stack < existing.m_shared.m_maxStackSize)
                .ToList();

            foreach (ItemDrop.ItemData target in partials)
            {
                if (item.m_stack <= 0 || !from.ContainsItem(item))
                {
                    break;
                }

                int space = target.m_shared.m_maxStackSize - target.m_stack;
                if (space <= 0)
                {
                    continue;
                }

                int amount = Mathf.Min(space, item.m_stack);
                anyAttempt = true;

                int done = MoveStack(from, to, item, amount, target.m_gridPos.x, target.m_gridPos.y);
                if (done > 0)
                {
                    moved += done;
                }
                else
                {
                    // Раньше здесь был break - один неудачный слот отменял весь перенос.
                    stats.MoveFailed++;
                    stats.FailedNames.Add(ItemLabel(item));
                }
            }

            // 2. Остаток кладём в свободные слоты того же сундука.
            if (QuickStackPlugin.FillEmptySlots.Value)
            {
                int guard = 0;
                while (item.m_stack > 0 && from.ContainsItem(item) && guard++ < 64)
                {
                    if (!FindEmptySlot(to, out int x, out int y))
                    {
                        if (item.m_stack > 0)
                        {
                            stats.MatchedButFull++;
                            stats.FullNames.Add(ItemLabel(item));
                        }

                        break;
                    }

                    int amount = Mathf.Min(item.m_stack, Mathf.Max(1, item.m_shared.m_maxStackSize));
                    anyAttempt = true;

                    int done = MoveStack(from, to, item, amount, x, y);
                    if (done <= 0)
                    {
                        stats.MoveFailed++;
                        stats.FailedNames.Add(ItemLabel(item));
                        break;
                    }

                    moved += done;
                }
            }
            else if (moved == 0 && anyAttempt == false)
            {
                stats.MatchedButFull++;
                stats.FullNames.Add(ItemLabel(item));
            }

            if (moved > 0 && QuickStackPlugin.VerboseLog.Value)
            {
                QuickStackPlugin.Log.LogInfo($"[quickstack] {ItemLabel(item)} -> {container.name}: {moved} шт.");
            }

            return moved;
        }

        /// <summary>
        /// Переносит amount штук в слот (x, y) и возвращает ФАКТИЧЕСКИ перенесённое количество.
        /// Не верим возвращаемому bool из MoveItemToThis: на части билдов он врёт,
        /// поэтому сверяем реальное количество в сундуке до и после.
        /// </summary>
        private static int MoveStack(Inventory from, Inventory to, ItemDrop.ItemData item, int amount, int x, int y)
        {
            if (amount <= 0)
            {
                return 0;
            }

            string key = item.m_shared.m_name;
            int before = CountByName(to, key);

            if (MoveItemToThisMethod != null)
            {
                try
                {
                    MoveItemToThisMethod.Invoke(to, new object[] { from, item, amount, x, y });

                    int delta = CountByName(to, key) - before;
                    if (delta > 0)
                    {
                        return delta;
                    }
                }
                catch (Exception e)
                {
                    QuickStackPlugin.Log.LogWarning(
                        $"MoveItemToThis недоступен ({e.Message}), переключаюсь на ручной перенос.");
                }
            }

            // Ванильный метод ничего не сделал - перекладываем вручную.
            if (MoveStackFallback(from, to, item, amount, x, y))
            {
                int delta = CountByName(to, key) - before;
                return delta > 0 ? delta : amount;
            }

            return 0;
        }

        private static int CountByName(Inventory inventory, string sharedName)
        {
            int total = 0;
            foreach (ItemDrop.ItemData item in inventory.GetAllItems())
            {
                if (item != null && item.m_shared != null && item.m_shared.m_name == sharedName)
                {
                    total += item.m_stack;
                }
            }

            return total;
        }

        /// <summary>Запасной путь на случай смены сигнатур или отказа ванильного метода.</summary>
        private static bool MoveStackFallback(Inventory from, Inventory to, ItemDrop.ItemData item, int amount, int x, int y)
        {
            ItemDrop.ItemData target = to.GetItemAt(x, y);

            if (target != null)
            {
                if (!IsSameItem(target, item))
                {
                    return false;
                }

                if (target.m_shared.m_maxStackSize - target.m_stack < amount)
                {
                    return false;
                }

                target.m_stack += amount;
            }
            else
            {
                ItemDrop.ItemData clone = item.Clone();
                clone.m_stack = amount;
                clone.m_gridPos = new Vector2i(x, y);

                if (!to.AddItem(clone))
                {
                    return false;
                }
            }

            from.RemoveItem(item, amount);
            return true;
        }

        private static bool FindEmptySlot(Inventory inventory, out int x, out int y)
        {
            int width = inventory.GetWidth();
            int height = inventory.GetHeight();

            for (int row = 0; row < height; row++)
            {
                for (int col = 0; col < width; col++)
                {
                    if (inventory.GetItemAt(col, row) == null)
                    {
                        x = col;
                        y = row;
                        return true;
                    }
                }
            }

            x = -1;
            y = -1;
            return false;
        }

        // ------------------------------------------------------------------ мелочи

        private static string ItemLabel(ItemDrop.ItemData item)
        {
            if (item == null || item.m_shared == null)
            {
                return "?";
            }

            string name = item.m_shared.m_name;
            try
            {
                if (Localization.instance != null)
                {
                    name = Localization.instance.Localize(name);
                }
            }
            catch
            {
                // ломать перенос из-за локализации не стоит
            }

            return item.m_quality > 1 ? $"{name} (ур.{item.m_quality})" : name;
        }

        private static HashSet<string> ParseList(string raw)
        {
            var set = new HashSet<string>();
            if (string.IsNullOrEmpty(raw))
            {
                return set;
            }

            foreach (string part in raw.Split(','))
            {
                string value = Normalize(part);
                if (value.Length > 0)
                {
                    set.Add(value);
                }
            }

            return set;
        }

        private static string Normalize(string value)
        {
            return string.IsNullOrEmpty(value) ? string.Empty : value.Trim().ToLowerInvariant();
        }

        private static void Message(Player player, string text)
        {
            if (!QuickStackPlugin.ShowMessage.Value || player == null)
            {
                return;
            }

            player.Message(MessageHud.MessageType.Center, text);
        }
    }
}
