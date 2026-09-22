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

        private static readonly MethodInfo ContainerSaveMethod =
            typeof(Container).GetMethod("Save", AllInstance, null, Type.EmptyTypes, null);

        private static readonly MethodInfo ContainerLoadMethod =
            typeof(Container).GetMethod("Load", AllInstance, null, Type.EmptyTypes, null);

        private static readonly MethodInfo MoveItemToThisMethod =
            typeof(Inventory).GetMethod(
                "MoveItemToThis", AllInstance, null,
                new[] { typeof(Inventory), typeof(ItemDrop.ItemData), typeof(int), typeof(int), typeof(int) },
                null);

        private static readonly FieldInfo WorldLevelField =
            typeof(ItemDrop.ItemData).GetField("m_worldLevel", AllInstance);

        private static int _layerMask;
        private static readonly Collider[] HitBuffer = new Collider[512];

        internal static void Run()
        {
            Player player = Player.m_localPlayer;
            if (player == null || player.IsDead() || player.InCutscene())
            {
                return;
            }

            Inventory playerInventory = player.GetInventory();
            if (playerInventory == null)
            {
                return;
            }

            HashSet<string> excludedItems = ParseList(QuickStackPlugin.ExcludedItems.Value);
            HashSet<string> excludedContainers = ParseList(QuickStackPlugin.ExcludedContainers.Value);

            List<Container> containers = FindNearbyContainers(player, excludedContainers);
            if (containers.Count == 0)
            {
                Message(player, "Рядом нет доступных сундуков");
                return;
            }

            int movedItems = 0;
            int usedContainers = 0;

            foreach (Container container in containers)
            {
                Inventory containerInventory = PrepareContainer(container);
                if (containerInventory == null)
                {
                    continue;
                }

                int movedHere = 0;

                foreach (ItemDrop.ItemData item in playerInventory.GetAllItems().ToList())
                {
                    if (!IsTransferable(item, excludedItems))
                    {
                        continue;
                    }

                    movedHere += StackItemIntoContainer(playerInventory, containerInventory, item);
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
                playerInventory.Changed();
                Message(player, $"Разложено {movedItems} шт. по {usedContainers} сундукам");
            }
            else
            {
                Message(player, "Нечего раскладывать");
            }
        }

        // ---------------------------------------------------------------- поиск сундуков

        private static List<Container> FindNearbyContainers(Player player, HashSet<string> excluded)
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

                if (!IsUsableContainer(container, playerId, excluded))
                {
                    continue;
                }

                result.Add(container);
            }

            result.Sort((a, b) =>
                Vector3.SqrMagnitude(a.transform.position - center)
                    .CompareTo(Vector3.SqrMagnitude(b.transform.position - center)));

            return result;
        }

        private static bool IsUsableContainer(Container container, long playerId, HashSet<string> excluded)
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
                return false;
            }

            // Приватный сундук другого игрока.
            if (!container.CheckAccess(playerId))
            {
                return false;
            }

            if (QuickStackPlugin.RespectWards.Value &&
                !PrivateArea.CheckAccess(container.transform.position, 0f, false, false))
            {
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

        /// <summary>Забираем владение ZDO и подтягиваем актуальное содержимое перед правкой.</summary>
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

        /// <summary>Сохраняем изменения в ZDO, чтобы их увидели сервер и другие игроки.</summary>
        private static void FinishContainer(Container container)
        {
            Inventory inventory = container.GetInventory();
            if (inventory != null)
            {
                inventory.Changed();
            }

            Invoke(ContainerSaveMethod, container, "Container.Save");
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

        private static bool IsTransferable(ItemDrop.ItemData item, HashSet<string> excluded)
        {
            if (item == null || item.m_shared == null || item.m_stack <= 0)
            {
                return false;
            }

            if (QuickStackPlugin.SkipEquipped.Value && item.m_equipped)
            {
                return false;
            }

            // Первый ряд инвентаря = хотбар.
            if (QuickStackPlugin.SkipFirstRow.Value && item.m_gridPos.y == 0)
            {
                return false;
            }

            if (item.m_shared.m_questItem)
            {
                return false;
            }

            if (!QuickStackPlugin.IncludeNonStackable.Value && item.m_shared.m_maxStackSize <= 1)
            {
                return false;
            }

            if (excluded.Count > 0)
            {
                string prefab = item.m_dropPrefab != null ? item.m_dropPrefab.name : string.Empty;
                if (excluded.Contains(Normalize(prefab)) ||
                    excluded.Contains(Normalize(item.m_shared.m_name)) ||
                    excluded.Contains(Normalize(Localization.instance.Localize(item.m_shared.m_name))))
                {
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

            // m_worldLevel появился в Ashlands - сравниваем через рефлексию, чтобы
            // мод компилировался и работал на любых версиях игры.
            if (WorldLevelField != null)
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
        private static int StackItemIntoContainer(Inventory from, Inventory to, ItemDrop.ItemData item)
        {
            if (!to.GetAllItems().Any(existing => IsSameItem(existing, item)))
            {
                return 0;
            }

            int moved = 0;

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
                if (!MoveStack(from, to, item, amount, target.m_gridPos.x, target.m_gridPos.y))
                {
                    break;
                }

                moved += amount;
            }

            // 2. Остаток кладём в свободные слоты того же сундука.
            if (QuickStackPlugin.FillEmptySlots.Value)
            {
                while (item.m_stack > 0 && from.ContainsItem(item) && FindEmptySlot(to, out int x, out int y))
                {
                    int amount = Mathf.Min(item.m_stack, Mathf.Max(1, item.m_shared.m_maxStackSize));
                    if (!MoveStack(from, to, item, amount, x, y))
                    {
                        break;
                    }

                    moved += amount;
                }
            }

            return moved;
        }

        private static bool MoveStack(Inventory from, Inventory to, ItemDrop.ItemData item, int amount, int x, int y)
        {
            if (amount <= 0)
            {
                return false;
            }

            if (MoveItemToThisMethod != null)
            {
                try
                {
                    object result = MoveItemToThisMethod.Invoke(to, new object[] { from, item, amount, x, y });
                    if (result is bool ok)
                    {
                        return ok;
                    }

                    return true;
                }
                catch (Exception e)
                {
                    QuickStackPlugin.Log.LogWarning(
                        $"MoveItemToThis недоступен ({e.Message}), переключаюсь на ручной перенос.");
                }
            }

            return MoveStackFallback(from, to, item, amount, x, y);
        }

        /// <summary>Запасной путь на случай смены сигнатур в новой версии игры.</summary>
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
