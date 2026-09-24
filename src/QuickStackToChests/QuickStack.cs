using System;
using System.Collections;
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

        /// <summary>На сколько метров за радиусом ещё ищем сундуки ради диагностики "ближайший".</summary>
        private const float DiagnosticBand = 20f;

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

        // Реестр всех загруженных сетевых объектов: Dictionary<ZDO, ZNetView>.
        // Берём через IDictionary, чтобы не зависеть от точных дженериков в разных версиях.
        private static readonly FieldInfo ZNetSceneInstancesField =
            typeof(ZNetScene).GetField("m_instances", AllInstance);

        /// <summary>Счётчики причин, почему сундук/предмет не подошли - без них отладка слепая.</summary>
        private sealed class Stats
        {
            internal string ScanMode = "-";
            internal int Scanned;
            internal int Inspected;
            internal int ContainersSeen;
            internal int ContainersFound;
            internal int ContainersInUse;
            internal int ContainersNoAccess;
            internal int ContainersNotLoaded;
            internal int ContainersNoInventory;
            internal int ContainersExcluded;
            internal int ContainersOnCharacter;
            internal float NearestDistance = float.MaxValue;
            internal string NearestName = "-";
            internal double ScanMs;
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
                Message(player, BuildNoContainersMessage(stats));
                LogSummary(stats, 0, 0);
                return;
            }

            int movedItems = 0;
            int usedContainers = 0;

            List<ItemDrop.ItemData> candidates = playerInventory.GetAllItems()
                .Where(item => IsTransferable(item, excludedItems, stats))
                .ToList();

            foreach (Container container in containers)
            {
                if (candidates.All(item => item.m_stack <= 0 || !playerInventory.ContainsItem(item)))
                {
                    break;
                }

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

        private static string BuildNoContainersMessage(Stats stats)
        {
            var reasons = new List<string>();
            if (stats.ContainersInUse > 0) reasons.Add($"открыты: {stats.ContainersInUse}");
            if (stats.ContainersNoAccess > 0) reasons.Add($"нет доступа: {stats.ContainersNoAccess}");
            if (stats.ContainersNotLoaded > 0) reasons.Add($"не загружены: {stats.ContainersNotLoaded}");
            if (stats.ContainersNoInventory > 0) reasons.Add($"без инвентаря: {stats.ContainersNoInventory}");
            if (stats.ContainersExcluded > 0) reasons.Add($"исключены: {stats.ContainersExcluded}");

            if (reasons.Count > 0)
            {
                return "Сундуки недоступны (" + string.Join(", ", reasons.ToArray()) + ")";
            }

            if (stats.NearestDistance < float.MaxValue)
            {
                return $"Сундуки есть, но дальше {QuickStackPlugin.Radius.Value:0} м (ближайший: {stats.NearestDistance:0.0} м)";
            }

            return "Рядом нет доступных сундуков";
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
                $"[quickstack] поиск={stats.ScanMode} за {stats.ScanMs:0.0} мс, объектов: {stats.Scanned}, проверено близких: {stats.Inspected}, " +
                $"сундуков: {stats.ContainersSeen}, годных: {stats.ContainersFound}, ближайший: {stats.NearestName} на " +
                $"{(stats.NearestDistance == float.MaxValue ? -1f : stats.NearestDistance):0.0} м, радиус: {QuickStackPlugin.Radius.Value:0} м");

            QuickStackPlugin.Log.LogInfo(
                $"[quickstack] сундуки отклонены [открыты: {stats.ContainersInUse}, нет доступа: {stats.ContainersNoAccess}, " +
                $"не загружены: {stats.ContainersNotLoaded}, без инвентаря: {stats.ContainersNoInventory}, " +
                $"на персонаже: {stats.ContainersOnCharacter}, исключены: {stats.ContainersExcluded}]");

            QuickStackPlugin.Log.LogInfo(
                $"[quickstack] перенесено: {moved} шт. в {usedContainers}; " +
                $"предметы пропущены [1й ряд: {stats.SkippedFirstRow}, надето: {stats.SkippedEquipped}, не стак: {stats.SkippedNonStackable}, " +
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

            var watch = System.Diagnostics.Stopwatch.StartNew();
            List<Container> found = CollectContainers(center, radius, stats);
            watch.Stop();
            stats.ScanMs = watch.Elapsed.TotalMilliseconds;

            foreach (Container container in found)
            {
                if (container == null || !seen.Add(container))
                {
                    continue;
                }

                stats.ContainersSeen++;

                float distance = Vector3.Distance(container.transform.position, center);
                if (distance < stats.NearestDistance)
                {
                    stats.NearestDistance = distance;
                    stats.NearestName = container.name;
                }

                if (distance > radius)
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

        /// <summary>
        /// Основной путь - обход ZNetScene.m_instances: там все загруженные сетевые объекты,
        /// без лимита буфера и без зависимости от слоёв и коллайдеров.
        /// Сначала дешёвая проверка расстояния, и только потом поиск компонентов:
        /// GetComponentInChildren обходит всю иерархию, и гонять его на каждой стене базы дорого.
        /// </summary>
        private static List<Container> CollectContainers(Vector3 center, float radius, Stats stats)
        {
            var containers = new List<Container>();
            float nearSqr = radius * radius;
            float bandSqr = (radius + DiagnosticBand) * (radius + DiagnosticBand);

            if (ZNetSceneInstancesField != null && ZNetScene.instance != null)
            {
                try
                {
                    if (ZNetSceneInstancesField.GetValue(ZNetScene.instance) is IDictionary instances)
                    {
                        foreach (object value in instances.Values)
                        {
                            stats.Scanned++;

                            if (!(value is ZNetView nview) || nview == null)
                            {
                                continue;
                            }

                            float distanceSqr = Vector3.SqrMagnitude(nview.transform.position - center);
                            if (distanceSqr > bandSqr)
                            {
                                continue;
                            }

                            stats.Inspected++;

                            // В радиусе - полный поиск; в диагностической полосе - только дешёвый GetComponent.
                            Container container = nview.GetComponent<Container>();
                            if (container == null && distanceSqr <= nearSqr)
                            {
                                container = nview.GetComponentInChildren<Container>();
                            }

                            if (container != null)
                            {
                                containers.Add(container);
                            }
                        }

                        stats.ScanMode = "znetscene";
                        return containers;
                    }
                }
                catch (Exception e)
                {
                    QuickStackPlugin.Log.LogWarning(
                        $"Обход ZNetScene.m_instances не удался ({e.Message}), использую физику.");
                }
            }

            // Запасной путь: аллокационный OverlapSphere по всем слоям, без обрезания.
            Collider[] hits = Physics.OverlapSphere(center, radius, ~0, QueryTriggerInteraction.Collide);
            foreach (Collider collider in hits)
            {
                stats.Scanned++;
                stats.Inspected++;

                if (collider == null)
                {
                    continue;
                }

                Container container = collider.GetComponentInParent<Container>();
                if (container != null)
                {
                    containers.Add(container);
                }
            }

            stats.ScanMode = "physics";
            return containers;
        }

        private static bool IsUsableContainer(Container container, long playerId, HashSet<string> excluded, Stats stats)
        {
            ZNetView nview = container.m_nview;
            if (nview == null || !nview.IsValid())
            {
                stats.ContainersNotLoaded++;
                return false;
            }

            // Инвентарь игрока/трупа/другого персонажа - не наш случай.
            if (container.GetComponentInParent<Character>() != null)
            {
                stats.ContainersOnCharacter++;
                return false;
            }

            if (container.GetInventory() == null)
            {
                stats.ContainersNoInventory++;
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
                    stats.ContainersExcluded++;
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
                    stats.MoveFailed++;
                    stats.FailedNames.Add(ItemLabel(item));
                }
            }

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
            else if (moved == 0 && !anyAttempt)
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
        /// Не верим bool из MoveItemToThis: на части билдов он врёт.
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
