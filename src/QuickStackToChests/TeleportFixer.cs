using System;
using System.Reflection;
using UnityEngine;

namespace QuickStackToChests
{
    /// <summary>
    /// Безопасное экстренное разбаговывание зависшей телепортации (бесконечный портальный тоннель/вихрь).
    /// </summary>
    public static class TeleportFixer
    {
        private static readonly FieldInfo FieldTeleporting = typeof(Player).GetField("m_teleporting", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo FieldTeleportTimer = typeof(Player).GetField("m_teleportTimer", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo FieldTeleportCooldown = typeof(Player).GetField("m_teleportCooldown", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo FieldTeleportFromPos = typeof(Player).GetField("m_teleportFromPos", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo FieldTeleportFromRot = typeof(Player).GetField("m_teleportFromRot", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo FieldTeleportTargetPos = typeof(Player).GetField("m_teleportTargetPos", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo FieldTeleportTargetRot = typeof(Player).GetField("m_teleportTargetRot", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo FieldMaxAirAltitude = typeof(Character).GetField("m_maxAirAltitude", BindingFlags.Instance | BindingFlags.NonPublic);

        /// <summary>
        /// Проверяет, застрял ли локальный игрок в процессе телепортации или на экране загрузки портала.
        /// </summary>
        public static bool IsPlayerStuckInTeleport()
        {
            Player player = Player.m_localPlayer;
            if (player == null)
            {
                return false;
            }

            bool isTeleporting = false;
            if (FieldTeleporting != null)
            {
                isTeleporting = (bool)FieldTeleporting.GetValue(player);
            }
            else
            {
                isTeleporting = player.IsTeleporting();
            }

            bool hudLoading = Hud.instance != null &&
                              Hud.instance.m_loadingScreen != null &&
                              Hud.instance.m_loadingScreen.gameObject.activeSelf;

            bool teleportProgress = Hud.instance != null &&
                                    Hud.instance.m_teleportingProgress != null &&
                                    Hud.instance.m_teleportingProgress.activeSelf;

            return isTeleporting || hudLoading || teleportProgress;
        }

        /// <summary>
        /// Безопасно снимает флаг телепортации, приземляет персонажа на поверхность и убирает черный/портальный экран.
        /// Защищено от случайного нажатия вне режима телепортации.
        /// </summary>
        /// <param name="returnToOrigin">Если true, возвращает к порталу отправления (если его координаты сохранены).</param>
        public static bool UnstickTeleport(bool returnToOrigin = false)
        {
            Player player = Player.m_localPlayer;
            if (player == null)
            {
                return false;
            }

            // Защита от случайного нажатия во время обычной игры
            if (!IsPlayerStuckInTeleport())
            {
                QuickStackPlugin.Log.LogInfo("[QuickStackToChests] TeleportFixer: player is not teleporting, skipping unstick.");
                return false;
            }

            try
            {
                Vector3 fromPos = Vector3.zero;
                Quaternion fromRot = Quaternion.identity;
                Vector3 targetPos = Vector3.zero;
                Quaternion targetRot = Quaternion.identity;

                if (FieldTeleportFromPos != null) fromPos = (Vector3)FieldTeleportFromPos.GetValue(player);
                if (FieldTeleportFromRot != null) fromRot = (Quaternion)FieldTeleportFromRot.GetValue(player);
                if (FieldTeleportTargetPos != null) targetPos = (Vector3)FieldTeleportTargetPos.GetValue(player);
                if (FieldTeleportTargetRot != null) targetRot = (Quaternion)FieldTeleportTargetRot.GetValue(player);

                Vector3 chosenPos = player.transform.position;
                Quaternion chosenRot = player.transform.rotation;

                if (returnToOrigin && fromPos != Vector3.zero)
                {
                    chosenPos = fromPos;
                    chosenRot = fromRot;
                }
                else if (targetPos != Vector3.zero)
                {
                    // Если игрок еще не был перемещен к цели (например, застрял на 1-2 секунде), используем цель
                    if ((player.transform.position - targetPos).sqrMagnitude > 4f)
                    {
                        chosenPos = targetPos;
                        chosenRot = targetRot;
                    }
                }

                // Коррекция высоты (приземление на пол или твердый грунт)
                if (ZoneSystem.instance != null && chosenPos != Vector3.zero)
                {
                    float floor = 0f;
                    if (ZoneSystem.instance.FindFloor(chosenPos, out floor))
                    {
                        chosenPos.y = floor + 0.15f;
                    }
                    else
                    {
                        float solid = ZoneSystem.instance.GetSolidHeight(chosenPos);
                        if (solid > -100f && solid < 5000f)
                        {
                            chosenPos.y = solid + 0.5f;
                        }
                    }
                }

                if (chosenPos != Vector3.zero)
                {
                    player.transform.position = chosenPos;
                    player.transform.rotation = chosenRot;
                }

                // Гасим инерцию и физику
                Rigidbody body = player.GetComponent<Rigidbody>();
                if (body != null)
                {
#if UNITY_2022_3_OR_NEWER || true
                    body.velocity = Vector3.zero;
                    body.angularVelocity = Vector3.zero;
#endif
                }

                // Сбрасываем внутренние флаги Player
                if (FieldTeleporting != null) FieldTeleporting.SetValue(player, false);
                if (FieldTeleportTimer != null) FieldTeleportTimer.SetValue(player, 0f);
                if (FieldTeleportCooldown != null) FieldTeleportCooldown.SetValue(player, 4f);
                if (FieldMaxAirAltitude != null) FieldMaxAirAltitude.SetValue(player, chosenPos.y);

                player.ResetCloth();

                if (EnvMan.instance != null)
                {
                    EnvMan.instance.ForceInstantEnvironmentSwitch();
                }

                // Принудительно выключаем портальный экран и полосы загрузки в Hud
                if (Hud.instance != null)
                {
                    if (Hud.instance.m_loadingScreen != null)
                    {
                        Hud.instance.m_loadingScreen.alpha = 0f;
                        Hud.instance.m_loadingScreen.gameObject.SetActive(false);
                    }
                    if (Hud.instance.m_teleportingProgress != null)
                    {
                        Hud.instance.m_teleportingProgress.SetActive(false);
                    }
                    if (Hud.instance.m_loadingProgress != null)
                    {
                        Hud.instance.m_loadingProgress.SetActive(false);
                    }
                    if (Hud.instance.m_sleepingProgress != null)
                    {
                        Hud.instance.m_sleepingProgress.SetActive(false);
                    }
                }

                string msg = returnToOrigin
                    ? Translations.Text("Teleport cancelled: returned to source portal", "Телепортация отменена: возврат к исходному порталу")
                    : Translations.Text("Teleport unstick applied successfully!", "Телепортация успешно разбагована!");

                player.Message(MessageHud.MessageType.Center, msg);
                QuickStackPlugin.Log.LogInfo($"[QuickStackToChests] TeleportFixer: Unstick applied at {chosenPos} (returnToOrigin: {returnToOrigin}).");
                return true;
            }
            catch (Exception ex)
            {
                QuickStackPlugin.Log.LogError($"[QuickStackToChests] TeleportFixer failed: {ex}");
                return false;
            }
        }
    }
}
