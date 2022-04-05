using BepInEx;
using BepInEx.Configuration;
using Nethereum.JsonRpc.UnityClient;
using Nethereum.RPC.Eth.DTOs;
using Nethereum.Util;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Xml.Serialization;
using UnityEngine;

namespace TheTaleOfToastPlugin
{
    [BepInPlugin("net.mindphlux.plugins.thetaleoftoastplugin", "The Tale of Toast Plugin", "1.1.0")]
    [BepInProcess("ToT.exe")]
    public class TheTaleOfToastPlugin : BaseUnityPlugin
    {
        private ConfigEntry<int> pinnedTradeSkill;
        private ConfigEntry<int> unknownLevelThreshold;
        private ConfigEntry<int> lastSummonedPet;
        private ConfigEntry<bool> showBagSpaceLabel;
        private ConfigEntry<bool> combatLogging;

        private float onDisplayLocationCounter;
        private bool canLogin;
        private Item dressingRoomItem;
        private bool mailboxFixed = false;
        private bool bagSpaceLabelAdded = false;
        private UILabel bagSpaceLabel;
        private bool discordInitialized = false;
        private DiscordRpc.RichPresence discordRichPresence;
        private string lastLocation;
        private long sessionStartTime = 0;
        private bool isFirewood = false;
        private decimal currentGameBalance;
        private bool repeatBalanceQuery = true;

        private void Awake()
        {
            unknownLevelThreshold = Config.Bind("General", "UnknownLevelThreshold", 10, "Set the threshold for displaying ?? instead of a level.");
            pinnedTradeSkill = Config.Bind("DoNotChange", "PinnedTradeSkill", -1, "The currently pinned trade skill (because the game doesn't save it)");
            lastSummonedPet = Config.Bind("DoNotChange", "LastSummonedPet", -1, "The last summoned vanity pet");
            showBagSpaceLabel = Config.Bind("General", "ShowBagSpaceLabel", true, "Displays free bag space near the button menu.");
            combatLogging = Config.Bind("General", "EnableCombatLogging", true, "Enable combat logging.");

            // hooks for quality of life fixes
            // more fixes are applied in the Update() method

            On.ContainerTradeSkills.PinSelectedTradeskill += SavePinnedTradeskill;
            On.ContainerPinnedTradeskill.XpUpdate += PinnedTradeskillXpUpdate;
            On.Map.DisplayLocation += OnDisplayLocation;
            On.PlayerTargeting.SetTarget += OnSetTarget;
            On.ContainerTargetInfo.Set_bool_bool_int_string_int_int_int_int_int_BaseStats_float_int += LevelConFix;
            On.PanelLogin.OnRealmSelected += OnRealmSelected;
            On.PanelLogin.Start += OnPanelLoginStart;
            On.ContainerTradeSkills.ShowRecipe += OnContainerTradeSkillsShowRecipe;
            On.ContainerMail.SetTab += OnContainerMailSetTab;
            On.PlayerPets.Summon += OnPlayerPetsSummon;
            On.PlayerPets.Dismiss += OnPlayerPetsDismiss;
            On.PlayerMovement.SetFlightPath += OnPlayerMovementSetFlightPath;

            if (showBagSpaceLabel.Value)
            {
                On.PlayerInventory.AddItem += OnPlayerInventoryAddItem;
                On.PlayerInventory.RemoveItem += OnPlayerInventoryRemoveItem;
                On.UIItemCursor.EquipVanityItem += OnUIItemCursorEquipVanityItem;
                On.UIItemCursor.UnequipVanityItem += OnUIItemCursorUnequipVanityItem;
                On.UIItemCursor.EquipItem += OnUIItemCursorEquipItem;
                On.UIItemCursor.UnequipItem += OnUIItemCursorUnequipItem;
            }

            On.DiscordController.UpdateDiscord += OnDiscordControllerUpdateDiscord;
            On.PlayerStats.LeveledUp += OnPlayerStatsLeveledUp;
            On.Map.DisplayLocation += OnMapDisplayLocation;
            On.ItemDatabase.Load_TextAsset += OnItemDatabaseLoadTextAsset;
            On.SocialManager.RefreshFriendRequestList += OnSocialManagerRefreshFriendRequestList;
            On.ContainerBuyDiamonds.BuyDiamonds += OnContainerBuyDiamondsBuyDiamonds;
            On.PanelGame.OnShow += OnPanelGameOnShow;
            On.PlayerStats.HPBReward += OnPlayerStatsHPBReward;
            On.UIItemTooltip.GetTooltipText += OnUIItemTooltipGetTooltipText;
            On.EnemyInit.Start += OnEnemyInitStart;
            On.ContainerHPB.Show += OnContainerHPBShow;
            On.HpbManager.GetBalance += OnHpbManagerGetBalance;
            On.ContainerHPB.Hide += OnContainerHPBHide;

            if (combatLogging.Value)
            {
                On.ChatManager.MessageFromServer += ChatManager_MessageFromServer;
            }

            sessionStartTime = (long)DateTime.UtcNow.Subtract(new DateTime(1970, 1, 1)).TotalSeconds;
        }

        private void Update()
        {
            if (GameManager.Instance.State == GameState.Login)
            {
                PressEnterToLogin();
            }

            if (GameManager.Instance.State == GameState.CharacterSelection)
            {
                PressEnterToEnterWorld();
            }

            if (GameManager.Instance.State != GameState.Game) return;

            if (!discordInitialized)
            {
                DiscordRpc.EventHandlers _handlers = new DiscordRpc.EventHandlers();
                DiscordRpc.Initialize("851053235758432256", ref _handlers, true, "640150");
                DiscordRpc.UpdatePresence(ref discordRichPresence);
                discordInitialized = true;
            }

            ResetMovementLogoutTimer();
            FixMinimapIcons();

            if (showBagSpaceLabel.Value)
            {
                if (!bagSpaceLabelAdded)
                {
                    bagSpaceLabelAdded = true;
                    StartCoroutine(BagSlotsOnButtonMenu());
                }
            }

            if (Input.GetKeyDown(KeyCode.F2))
            {
                SettingsManager.Instance.ToggleLockActionBars();
                bool _locked = SettingsManager.Instance.LockActionBars;
                Chat.Instance.InfoMessage(ChatMessageType.Info, $"Action bars are now {(_locked ? "locked." : "unlocked.")}");
            }

            if (Input.GetKeyDown(KeyCode.F3))
            {
                if (showBagSpaceLabel.Value)
                {
                    showBagSpaceLabel.Value = false;
                    bagSpaceLabel.enabled = false;
                }
                else
                {
                    showBagSpaceLabel.Value = true;
                    bagSpaceLabel.enabled = true;
                }
            }

            if (Input.GetKeyUp(KeyCode.F4))
            {
                BeautifyEffect.Beautify _beautify = FindObjectOfType<BeautifyEffect.Beautify>();
                _beautify.sharpen = _beautify.sharpen == 3f ? 0f : 3f;
                Chat.Instance.InfoMessage(ChatMessageType.Info, $"Sharpen effect is now {(_beautify.sharpen == 3 ? "enabled." : "disabled.")}");
            }
        }

        #region Combat logging
        private void ChatManager_MessageFromServer(On.ChatManager.orig_MessageFromServer orig, ChatManager self, int chatMessageType, string message, int chatTab)
        {
            if (chatTab == 2 || chatTab == 3)
            {
                AddCombatLogLine(string.Format("{0}\n", message));
            }
            orig.Invoke(self, chatMessageType, message, chatTab);
        }

        private void AddCombatLogLine(string _line)
        {
            DateTime _date = DateTime.Now;
            Directory.CreateDirectory("Logs");
            string _month = _date.Month < 10 ? string.Format("{0}{1}", "0", _date.Month) : _date.Month.ToString();
            string _day = _date.Day < 10 ? string.Format("{0}{1}", "0", _date.Day) : _date.Day.ToString();

            File.AppendAllText(string.Format("Logs\\Combat-{0}.log", string.Format("{0}-{1}-{2}", _date.Year, _month, _day)), string.Format("[{0}] {1}", _date.ToLongTimeString(), _line));
        }

        #endregion Combat logging

        #region Bug fixes

        #region Fix for broken mail box back button

        private void OnContainerMailSetTab(On.ContainerMail.orig_SetTab orig, ContainerMail self, MailTab tab)
        {
            // the "Back" button on the Compose tab of the mailbox had a wrong (and not even existing) method bound to its
            // onClick event. This fixes the problem and also unbinds the wrongly bound method so no more errors appear in the console.

            orig.Invoke(self, tab);

            if (mailboxFixed) return;

            if (tab == MailTab.Compose)
            {
                UIButton[] _buttons = self.gameObject.GetComponentsInChildren<UIButton>();
                UIButton _backButton = null;

                for (int i = 0; i < _buttons.Length; i++)
                {
                    if (_buttons[i].name == "Button_Back")
                    {
                        _backButton = _buttons[i];
                    }
                }

                if (_backButton != null)
                {
                    EventDelegate MailBoxBackButtonFix = new EventDelegate(this, "MailBoxBackButtonFix");
                    _backButton.onClick.Clear();
                    _backButton.onClick.Add(MailBoxBackButtonFix);
                    mailboxFixed = true;
                }
            }
        }

        private void MailBoxBackButtonFix()
        {
            GuiManager.Instance.panelGame.containerMail.SetTab(MailTab.List);
        }

        #endregion Fix for broken mail box back button

        #region Fix for missing item icons

        private ItemDatabase OnItemDatabaseLoadTextAsset(On.ItemDatabase.orig_Load_TextAsset orig, TextAsset xmlFile)
        {
            // fixes the missing icon for item "Dusty Tome" (id 3675) and "Cooked Enriched Delicious Meat" (2488)

            try
            {
                ItemDatabase _base = (ItemDatabase)new XmlSerializer(typeof(ItemDatabase)).Deserialize(new StringReader(xmlFile.text));

                for (int i = 0; i < _base.Items.Count; i++)
                {
                    if (_base.Items[i].id == 3675)
                    {
                        _base.Items[i].iconAtlas = "ItemAtlas_Parts";
                        _base.Items[i].icon = "pt_t_18";
                    }
                    if (_base.Items[i].id == 2488)
                    {
                        _base.Items[i].icon = "meat_f_03_magic";
                    }
                    if (_base.Items[i].id == 3048)
                    {
                        _base.Items[i].icon = "pt_t_06";
                    }
                    if (_base.Items[i].id == 3047)
                    {
                        _base.Items[i].icon = "pt_t_06";
                    }
                }

                return _base;
            }
            catch (Exception arg)
            {
                Debug.LogError("[ItemDatabase] Failed to load Item DB: " + arg);
                ToastyTools.Quit();
            }
            return null;
        }

        #endregion Fix for missing item icons

        #region Discord Rich Presence

        private void CustomUpdateDiscord(string playerLevel, string playerName = null, string playerLocation = null)
        {
            if (playerName == null)
            {
                playerName = PlayerCommon.Instance.Init.PlayerName;
            }

            if (playerLocation == null)
            {
                playerLocation = "Astaria";
            }

            if (discordInitialized)
            {
                discordRichPresence.largeImageKey = "toastlogo";
                discordRichPresence.largeImageText = "The Tale of Toast";
                discordRichPresence.smallImageKey = "toastlogo";
                discordRichPresence.smallImageText = "The Tale of Toast";
                discordRichPresence.state = $"Adventuring in {playerLocation}";
                discordRichPresence.details = $"{playerName}, level {playerLevel}";
                discordRichPresence.startTimestamp = sessionStartTime;
                DiscordRpc.UpdatePresence(ref discordRichPresence);
            }
        }

        private void OnDiscordControllerUpdateDiscord(On.DiscordController.orig_UpdateDiscord orig, DiscordController self, string largeImage, string realm, string smallImage, string playerName, string playerLevel)
        {
            CustomUpdateDiscord(playerLevel, playerName, lastLocation);
        }

        private void OnMapDisplayLocation(On.Map.orig_DisplayLocation orig, Map self, string locationName, string subName, bool forcePvp, bool pvpAllowed, bool pardondedZone, int pvpLevelGap)
        {
            orig.Invoke(self, locationName, subName, forcePvp, pvpAllowed, pardondedZone, pvpLevelGap);
            CustomUpdateDiscord(PlayerCommon.Instance.Stats.CurrentLevel.ToString(), null, locationName);
            lastLocation = locationName;
        }

        private void OnPlayerStatsLeveledUp(On.PlayerStats.orig_LeveledUp orig, PlayerStats self, int level, int availablePoints, int pointsGained)
        {
            orig.Invoke(self, level, availablePoints, pointsGained);
            CustomUpdateDiscord(level.ToString(), null, lastLocation);
        }

        #endregion Discord Rich Presence

        #region Reposition enemy name plates relative to their model's height

        private void OnEnemyInitStart(On.EnemyInit.orig_Start orig, EnemyInit self)
        {
            if (uLink.Network.isClient)
            {
                GameObject _go = Instantiate(self.nameplatePrefab);
                _go.transform.SetParent(self.transform);
                Renderer _renderer = self.GetComponentInChildren<Renderer>();

                // for models that are wider than high we're moving the nameplate a bit higher
                // as these models are usually used by flying enemies and the nameplate would otherwise
                // clip into the animation

                _go.transform.localPosition = new Vector3(0f, _renderer.bounds.size.y + (_renderer.bounds.size.y > _renderer.bounds.size.x ? 0.1f : 0.3f), 0f);
            }
        }

        #endregion Reposition enemy name plates relative to their model's height

        #endregion Bug fixes

        #region Quality of Life Fixes

        #region Bag space label

        private void OnUIItemCursorUnequipItem(On.UIItemCursor.orig_UnequipItem orig, int itemId, bool vanity)
        {
            orig.Invoke(itemId, vanity);
            UpdateBagSpaceLabel();
        }

        private void OnUIItemCursorEquipItem(On.UIItemCursor.orig_EquipItem orig)
        {
            orig.Invoke();
            UpdateBagSpaceLabel();
        }

        private void OnUIItemCursorUnequipVanityItem(On.UIItemCursor.orig_UnequipVanityItem orig, int itemId)
        {
            orig.Invoke(itemId);
            UpdateBagSpaceLabel();
        }

        private void OnUIItemCursorEquipVanityItem(On.UIItemCursor.orig_EquipVanityItem orig)
        {
            orig.Invoke();
            UpdateBagSpaceLabel();
        }

        private IEnumerator BagSlotsOnButtonMenu()
        {
            yield return new WaitForSeconds(3f);

            try
            {
                GameObject _go = FindObjectOfType<ContainerButtonMenu>().gameObject;
                UILabel[] _label = _go.GetComponentsInChildren<UILabel>();

                UILabel _labelToClone = null;

                for (int i = 0; i < _label.Length; i++)
                {
                    if (_label[i].name == "Label_Diamonds")
                    {
                        _labelToClone = _label[i];
                    }
                }

                if (_labelToClone != null)
                {
                    Transform _parentTransform = null;

                    UISprite[] _sprites = _go.GetComponentsInChildren<UISprite>();

                    for (int i = 0; i < _sprites.Length; i++)
                    {
                        if (_sprites[i].name == "Button_ToggleMenu")
                        {
                            _parentTransform = _sprites[i].transform;
                        }
                    }

                    if (_parentTransform != null)
                    {
                        bagSpaceLabel = Instantiate(_labelToClone, _go.transform);
                        bagSpaceLabel.transform.localPosition = new Vector2(-112f, -73f);
                        bagSpaceLabel.name = "Label_BagSpace";
                        UpdateBagSpaceLabel();
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        private void UpdateBagSpaceLabel()
        {
            try
            {
                int _emptySlots = PlayerInventory.Instance.Inventory.EmptySlotCount();
                bagSpaceLabel.text = $"{_emptySlots}/25";

                if (_emptySlots < 3)
                {
                    bagSpaceLabel.color = Color.red;
                }
                else if (_emptySlots > 3 && _emptySlots < 15)
                {
                    bagSpaceLabel.color = Color.yellow;
                }
                else if (_emptySlots > 15)
                {
                    bagSpaceLabel.color = Color.green;
                }
            }
            catch (Exception) { }
        }

        private void OnPlayerInventoryRemoveItem(On.PlayerInventory.orig_RemoveItem orig, PlayerInventory self, int slot, int amount)
        {
            orig.Invoke(self, slot, amount);
            UpdateBagSpaceLabel();
        }

        private void OnPlayerInventoryAddItem(On.PlayerInventory.orig_AddItem orig, PlayerInventory self, int itemId, int slot, int amount, string namePrefix, int craftQuality, int itemLevel, int adjective, int attribute, string seed, bool fromBank, bool broken)
        {
            orig.Invoke(self, itemId, slot, amount, namePrefix, craftQuality, itemLevel, adjective, attribute, seed, fromBank, broken);
            UpdateBagSpaceLabel();
        }

        #endregion Bag space label

        #region Fix for missing Alt-Click to preview crafted items

        private void OnContainerTradeSkillsShowRecipe(On.ContainerTradeSkills.orig_ShowRecipe orig, ContainerTradeSkills self, string recipeName)
        {
            // on the original Trade Skills window it isn't possible to view the crafted item in the dressing room
            // despite the tooltip claiming Alt+Click would preview it.
            // to make this work we'll add an UIButton component to the item icon and route the onClick handler to our custom function

            orig.Invoke(self, recipeName);

            dressingRoomItem = ItemManager.Instance.GetItem(recipeName);

            if (self.recipeIcon.gameObject.GetComponent<UIButton>()) return;

            UIButton _button = self.recipeIcon.gameObject.AddComponent<UIButton>();
            EventDelegate DressingRoomPreview = new EventDelegate(this, "ShowDressingRoom");
            _button.onClick.Add(DressingRoomPreview);
        }

        private void ShowDressingRoom()
        {
            if (Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt))
            {
                GuiManager.Instance.panelGame.containerDressingRoom.Show();

                // while there is a method calld DressingRoom.ShowEquipment(item) it doesn't work
                // so we'll use DressingRoom.ShowVanity(item) which does work. No idea why ...

                PlayerCommon.Instance.DressingRoom.ShowVanity(dressingRoomItem);
            }
        }

        #endregion Fix for missing Alt-Click to preview crafted items

        #region Add plugin version to the login screen

        private void OnPanelLoginStart(On.PanelLogin.orig_Start orig, PanelLogin self)
        {
            canLogin = false;
            orig.Invoke(self);
            var _m = MetadataHelper.GetMetadata(this);
            self.labelBuildVersion.width = 300;
            self.labelBuildVersion.text += $" (Plugin v{_m.Version.Major}.{_m.Version.Minor}.{_m.Version.Build})";
        }

        #endregion Add plugin version to the login screen

        #region Show error message when trying to buy Crumbs with the Steam Overlay not enabled

        private void OnContainerBuyDiamondsBuyDiamonds(On.ContainerBuyDiamonds.orig_BuyDiamonds orig, ContainerBuyDiamonds self)
        {
            if (SteamManager.SteamClient.Overlay.Enabled)
            {
                orig.Invoke(self);
            }
            else
            {
                GuiManager.Instance.ShowMessage("Error", "You need to start the game via Steam to be able to buy Crumbs.");
            }
        }

        #endregion Show error message when trying to buy Crumbs with the Steam Overlay not enabled

        #region Level con fix

        private void LevelConFix(On.ContainerTargetInfo.orig_Set_bool_bool_int_string_int_int_int_int_int_BaseStats_float_int orig, ContainerTargetInfo self, bool enabled, bool attackable, int playerLevel, string targetName, int targetLevel, int targetHealth, int targetMaxHealth, int targetMana, int targetMaxMana, BaseStats targetStats, float distance, int threat)
        {
            // set the threshold for displaying ?? as level to an user defined value instead of 6
            // defaults to 10 levels like in most games

            GameManager.Instance.unknownLevelThreshold = unknownLevelThreshold.Value;
            orig.Invoke(self, enabled, attackable, playerLevel, targetName, targetLevel, targetHealth, targetMaxHealth, targetMana, targetMaxMana, targetStats, distance, threat);
        }

        #endregion Level con fix

        #region Disable self-targeting

        private void OnSetTarget(On.PlayerTargeting.orig_SetTarget orig, PlayerTargeting self, ITargetable newTarget)
        {
            // fixes the problem that right-clicking the player targets them
            // which can cause issues in combat and is also rather irritating

            if (newTarget.TargetName == PlayerCommon.Instance.Init.PlayerName) return;

            /*
            if(inspectWindow.activeInHierarchy && !dontInspectMe.Value)
            {
                ShowInspectWindow();
            }
            */

            orig.Invoke(self, newTarget);
        }

        #endregion Disable self-targeting

        #region Hook for anything that needs the game to be initialized to work

        private void OnDisplayLocation(On.Map.orig_DisplayLocation orig, Map self, string locationName, string subName, bool forcePvp, bool pvpAllowed, bool pardondedZone, int pvpLevelGap)
        {
            // we are hooking Map.DisplayLocation() because it's called rather late and everything is already initialized at this point
            // because this method is actually called twice upon entering the world we only apply the fix on the first call

            orig.Invoke(self, locationName, subName, forcePvp, pvpAllowed, pardondedZone, pvpLevelGap);
            onDisplayLocationCounter++;
            if (onDisplayLocationCounter == 1)
            {
                StartCoroutine(RestorePinnedTradeSkill());
                ResummonPetAfterLogin();
            }
        }

        #endregion Hook for anything that needs the game to be initialized to work

        #region Save/restore pinned tradeskill

        private void PinnedTradeskillXpUpdate(On.ContainerPinnedTradeskill.orig_XpUpdate orig, ContainerPinnedTradeskill self, TradeSkill skill, int level, uint levelXp, uint nextLevelXp, uint totalXp)
        {
            if (skill == (TradeSkill)pinnedTradeSkill.Value)
            {
                UILabel _label = GuiManager.Instance.panelGame.containerPinnedTradeskill.GetComponentInChildren<UILabel>();
                _label.text = ((TradeSkill)pinnedTradeSkill.Value).ToString() + " " + level;

                orig.Invoke(self, skill, level, levelXp, nextLevelXp, totalXp);
            }
        }

        private void SavePinnedTradeskill(On.ContainerTradeSkills.orig_PinSelectedTradeskill orig, ContainerTradeSkills self)
        {
            // saves the currently pinned trade skill to the plugin's config file

            orig.Invoke(self);
            TradeSkill _tradeskill = GuiManager.Instance.panelGame.containerPinnedTradeskill.Skill;
            pinnedTradeSkill.Value = (int)_tradeskill;
            StartCoroutine(RestorePinnedTradeSkill(0.5f));
        }

        private IEnumerator RestorePinnedTradeSkill(float _delay = 5f)
        {
            // restores the pinned trade skill from the plugin's config file
            // for safety reasons we defer the execution by 5 seconds to make sure all values are actually initialized

            yield return new WaitForSeconds(_delay);

            if (pinnedTradeSkill.Value != -1)
            {
                TradeSkill _tradeSkill = (TradeSkill)pinnedTradeSkill.Value;

                int _level = PlayerCommon.Instance.TradeSkills.Level(_tradeSkill);
                uint _xpNow = PlayerCommon.Instance.TradeSkills.Experience[_tradeSkill] - XpManager.Instance.ConvertLevelToXp(PlayerCommon.Instance.TradeSkills.Level(_tradeSkill));
                uint _xpNext = XpManager.Instance.ConvertLevelToXp(PlayerCommon.Instance.TradeSkills.Level(_tradeSkill) + 1);
                uint _totalNow = PlayerCommon.Instance.TradeSkills.Experience[_tradeSkill];

                GuiManager.Instance.panelGame.containerPinnedTradeskill.Set((TradeSkill)pinnedTradeSkill.Value, _level, _xpNow, (_xpNext - _totalNow) + _xpNow, 0);
                GuiManager.Instance.panelGame.containerPinnedTradeskill.gameObject.SetActive(true);
            }
        }

        #endregion Save/restore pinned tradeskill

        #region Scales minimap icons to about 65% of their original size on the highest zoom level

        private void FixMinimapIcons()
        {
            // fixes the way too large minimap icons on the highest zoom level

            GameObject[] _icons = GameObject.FindObjectsOfType<GameObject>();
            for (int i = 0; i < _icons.Length; i++)
            {
                if (_icons[i].GetComponent<CraftStation>() ||
                    _icons[i].GetComponent<NpcCommon>() ||
                    _icons[i].GetComponent<Mailbox>() ||
                    _icons[i].GetComponent<LootableObject>() ||
                    _icons[i].GetComponent<NpcFlightMaster>()
                )
                {
                    if (!_icons[i].GetComponent<PhatRobit.MiniMapIcon>()) continue;
                    if (_icons[i].GetComponent<NpcCommon>() &&
                        (!_icons[i].GetComponent<NpcVendor>().isEnabled &&
                        !_icons[i].GetComponent<NpcFlightMaster>().isEnabled)) continue;

                    if (PhatRobit.MiniMap.Instance.CurrentZoom == PhatRobit.MiniMap.Instance.minDistance)
                    {
                        _icons[i].GetComponent<PhatRobit.MiniMapIcon>().SetScale(3f);
                    }
                    else
                    {
                        _icons[i].GetComponent<PhatRobit.MiniMapIcon>().SetScale(5f);
                    }
                }
            }
        }

        #endregion Scales minimap icons to about 65% of their original size on the highest zoom level

        #region Enter/Return key to enter world on character selection screen

        private void PressEnterToEnterWorld()
        {
            // adds the ability to enter the world by pressing Return or Enter on the character selection screen
            // like World of Warcraft does it

            if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
            {
                GuiManager.Instance.panelCharacterSelection.EnterWorld();
            }
        }

        #endregion Enter/Return key to enter world on character selection screen

        #region Enter/return key to log in

        private void OnRealmSelected(On.PanelLogin.orig_OnRealmSelected orig, PanelLogin self)
        {
            orig.Invoke(self);
            if (UserManager.Instance.Realm != "") canLogin = true;
        }

        private void PressEnterToLogin()
        {
            // adds the ability to login by pressing Return or Enter on the login screen

            if (!canLogin) return;

            if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
            {
                SteamManager.Instance.TryLogin();
            }
        }

        #endregion Enter/return key to log in

        #region Disable movement logout timer

        private void ResetMovementLogoutTimer()
        {
            // disables the 10 second logout timer after the character has been moved
            // the timer is completely pointless since you could just use Alt+F4 to exit instantly

            try
            {
                PlayerMovement.Instance.Movement.LogoutTimer = 0f;
            }
            catch (Exception) { }
        }

        #endregion Disable movement logout timer

        #region Resummon pet after flight/logout

        private void OnPlayerPetsDismiss(On.PlayerPets.orig_Dismiss orig, PlayerPets self)
        {
            // resets the stored last summoned pet after the player dismisses his current pet

            orig.Invoke(self);
            lastSummonedPet.Value = -1;
        }

        private void OnPlayerPetsSummon(On.PlayerPets.orig_Summon orig, PlayerPets self, int itemID)
        {
            // saves the currently summoned pet after summoning it

            orig.Invoke(self, itemID);
            lastSummonedPet.Value = itemID;
        }

        private void OnPlayerMovementSetFlightPath(On.PlayerMovement.orig_SetFlightPath orig, PlayerMovement self, bool enabled)
        {
            orig.Invoke(self, enabled);

            if (enabled)
            {
                StartCoroutine(ResummonPetAfterFlying(self));
            }
        }

        private IEnumerator ResummonPetAfterFlying(PlayerMovement _movement)
        {
            // automatically resummons the last summoned pet after completing a flight path

            while (_movement.isOnFlightPath)
            {
                yield return new WaitForEndOfFrame();
            }

            if (lastSummonedPet.Value != -1)
            {
                PlayerCommon.Instance.Pets.SendSummonRequest(lastSummonedPet.Value);
            }
        }

        private void ResummonPetAfterLogin()
        {
            // automatically resummons the last summoned pet after logging in

            if (lastSummonedPet.Value != -1)
            {
                PlayerCommon.Instance.Pets.SendSummonRequest(lastSummonedPet.Value);
            }
        }

        #endregion Resummon pet after flight/logout

        #region Friend request notification via chat message

        private void OnSocialManagerRefreshFriendRequestList(On.SocialManager.orig_RefreshFriendRequestList orig, SocialManager self, string rawFriendList)
        {
            // notifies the player in chat if there's a friend request waiting for him

            orig.Invoke(self, rawFriendList);
            if (self.FriendRequestList.Count > 0)
            {
                if (SettingsManager.Instance.PlayerKeys.ToggleSocial.Bindings.Count > 0)
                {
                    Chat.Instance.InfoMessage(ChatMessageType.Info, $"You have {self.FriendRequestList.Count} pending friend {(self.FriendRequestList.Count > 1 ? "requests." : "request.")}. Press {SettingsManager.Instance.PlayerKeys.ToggleSocial.Bindings[0].Name} to manage {(self.FriendRequestList.Count > 1 ? "them." : "it.")})");
                }
                else
                {
                    Chat.Instance.InfoMessage(ChatMessageType.Info, $"You have {self.FriendRequestList.Count} pending friend {(self.FriendRequestList.Count > 1 ? "requests." : "request.")}");
                }
            }
        }

        #endregion Friend request notification via chat message

        #region Adds plugin active message to chat

        private void OnPanelGameOnShow(On.PanelGame.orig_OnShow orig, PanelGame self)
        {
            orig.Invoke(self);
            var _m = MetadataHelper.GetMetadata(this);
            Chat.Instance.InfoMessage(ChatMessageType.Info, $"TaleOfToastPlugin v{_m.Version.Major}.{_m.Version.Minor}.{_m.Version.Build} enabled.");
            RemoveHPB();
        }

        #endregion Adds plugin active message to chat

        #region Removes any remaining mention of the (defunct) HPB crypto currency system

        private void RemoveHPB()
        {
            // Removes "HPB death cloud" effect on loot/kill

            LightIntensityFade[] _go = FindObjectsOfType<LightIntensityFade>();
            foreach (LightIntensityFade _o in _go)
            {
                if (_o.gameObject.name == "effectEnemyDeathCloudHPB")
                {
                    Destroy(_o.gameObject);
                    break;
                }
            }
        }

        private void OnPlayerStatsHPBReward(On.PlayerStats.orig_HPBReward orig, PlayerStats self)
        {
            return;
        }

        #endregion Removes any remaining mention of the (defunct) HPB crypto currency system

        #region Adds crafted item to crafting recipe tooltip, also adds actual effect to potions tooltip

        private string OnUIItemTooltipGetTooltipText(On.UIItemTooltip.orig_GetTooltipText orig, UIItemTooltip self, Item item, int stackCount, bool isVendor, bool isSelling, bool isTrading, bool isInTradeWindow, bool multiple, bool isEquipped, bool isInChatWindow, bool isInCraftWindow)
        {
            return GetCustomItemTooltipText(item, stackCount, isVendor, isSelling, isTrading, isInTradeWindow, multiple, isEquipped, isInChatWindow, isInCraftWindow);
        }

        private string GetCustomItemTooltipText(Item item, int stackCount, bool isVendor = false, bool isSelling = false, bool isTrading = false, bool isInTradeWindow = false, bool multiple = false, bool isEquipped = false, bool isInChatWindow = false, bool isInCraftWindow = false, bool isInline = false)
        {
            StringBuilder stringBuilder = new StringBuilder();
            string text = "[" + ItemManager.Instance.GetItemHex(item) + "]";
            stringBuilder.Append(text + item.Name + "[-]");
            string flavor = item.Flavor;
            stringBuilder.Append(string.Concat(new object[]
            {
                "\n",
                text,
                (flavor.Length > 0) ? (flavor + " ") : "",
                item.quality,
                "[-]"
            }));
            if (item.itemType == ItemType.Armor)
            {
                stringBuilder.Append("\n");
                if (!item.isDiamondItem && item.armorType != ArmorType.None)
                {
                    stringBuilder.Append(item.armorType + " ");
                }
                else if (item.isDiamondItem && item.armorSlot != ArmorSlot.None)
                {
                    stringBuilder.Append("Cosmetic ");
                }
                stringBuilder.Append(ToastyTools.AddSpacesNearUpper(item.armorSlot.ToString()));
                if (item.Armor > 0)
                {
                    if (item.craftQuality > 0)
                    {
                        stringBuilder.Append("[00FF00]");
                    }
                    stringBuilder.Append("\n" + item.Armor);
                    if (item.craftQuality > 0)
                    {
                        stringBuilder.Append("[-]");
                    }
                    stringBuilder.Append(" Armor");
                }
                if (item.MagicResist > 0)
                {
                    if (item.craftQuality > 0)
                    {
                        stringBuilder.Append("[00FF00]");
                    }
                    stringBuilder.Append("\n" + item.MagicResist);
                    if (item.craftQuality > 0)
                    {
                        stringBuilder.Append("[-]");
                    }
                    stringBuilder.Append(" Magic Resist");
                }
                if (item.HitRating > 0)
                {
                    stringBuilder.Append("\nHit Rating: ");
                    if (item.craftQuality > 0)
                    {
                        stringBuilder.Append("[00FF00]");
                    }
                    stringBuilder.Append(item.HitRating);
                    if (item.craftQuality > 0)
                    {
                        stringBuilder.Append("[-]");
                    }
                }
                if (isInCraftWindow)
                {
                    stringBuilder.Append("\nRandom Attribute Chances:");
                    if (item.IsVitalityItem())
                    {
                        stringBuilder.Append("\n| Vitality");
                    }
                    if (item.IsDefenseItem())
                    {
                        stringBuilder.Append("\n| Defense");
                    }
                    if (item.IsStrongItem())
                    {
                        stringBuilder.Append("\n| Strength");
                    }
                    if (item.IsWiseItem())
                    {
                        stringBuilder.Append("\n| Wisdom");
                    }
                    if (item.IsRangedItem())
                    {
                        stringBuilder.Append("\n| Dexterity");
                    }
                    if (item.IsMagicItem())
                    {
                        stringBuilder.Append("\n| Magic");
                    }
                    if (item.IsCraftingItem())
                    {
                        stringBuilder.Append("\n| Crafting");
                    }
                }
            }
            if (item.itemType == ItemType.Weapon)
            {
                stringBuilder.Append("\n" + ToastyTools.AddSpacesNearUpper(item.weaponSlot.ToString()));
                if (item.WeaponSpeed > 0f)
                {
                    stringBuilder.Append("\nSpeed: " + item.WeaponSpeed);
                }
                if (item.Armor > 0)
                {
                    if (item.craftQuality > 0)
                    {
                        stringBuilder.Append("[00FF00]");
                    }
                    stringBuilder.Append("\n" + item.Armor + " Armor");
                    if (item.craftQuality > 0)
                    {
                        stringBuilder.Append("[-]");
                    }
                }
                if (item.weaponClass != WeaponClass.Shield)
                {
                    stringBuilder.Append("\n" + ToastyTools.AddSpacesNearUpper(item.weaponType.ToString()));
                    if (item.rangedWeaponType != RangedWeaponType.None)
                    {
                        stringBuilder.Append(" " + item.rangedWeaponType);
                    }
                    stringBuilder.Append(" " + ToastyTools.AddSpacesNearUpper(item.weaponClass.ToString()));
                }
                if (item.CanShootProjectiles())
                {
                    stringBuilder.Append("\nRange: " + item.Range + " meters");
                }
                if (item.weaponClass != WeaponClass.Shield && item.MinDamage != 0 && item.MaxDamage != 0)
                {
                    if (item.craftQuality > 0)
                    {
                        stringBuilder.Append("[00FF00]");
                    }
                    stringBuilder.Append(string.Concat(new object[]
                    {
                "\n",
                item.MinDamage,
                " - ",
                item.MaxDamage,
                " Damage"
                    }));
                    if (item.craftQuality > 0)
                    {
                        stringBuilder.Append("[-]");
                    }
                }
                if (item.HitRating > 0)
                {
                    stringBuilder.Append("\n[00FF00]Hit Rating: " + item.HitRating + "[-]");
                }
                if (isInCraftWindow)
                {
                    stringBuilder.Append("\nRandom Attribute Chances:");
                    if (item.IsVitalityItem())
                    {
                        stringBuilder.Append("\n| Vitality");
                    }
                    if (item.IsDefenseItem())
                    {
                        stringBuilder.Append("\n| Defense");
                    }
                    if (item.IsStrongItem())
                    {
                        stringBuilder.Append("\n| Strength");
                    }
                    if (item.IsWiseItem())
                    {
                        stringBuilder.Append("\n| Wisdom");
                    }
                    if (item.IsRangedItem())
                    {
                        stringBuilder.Append("\n| Dexterity");
                    }
                    if (item.IsMagicItem())
                    {
                        stringBuilder.Append("\n| Magic");
                    }
                    if (item.IsCraftingItem())
                    {
                        stringBuilder.Append("\n| Crafting");
                    }
                }
            }
            if (item.itemType == ItemType.TradeTool)
            {
                stringBuilder.Append("\n" + ToastyTools.AddSpacesNearUpper(item.weaponClass.ToString()));
            }
            if (item.itemType == ItemType.Consumable)
            {
                stringBuilder.Append("\n" + item.itemType);
                if (item.consumableHealthAmount > 0)
                {
                    stringBuilder.Append("\n\n[00FF00]Use: Restores " + item.consumableHealthAmount + " Health");
                    if (item.consumableManaAmount > 0)
                    {
                        stringBuilder.Append(" and" + ((item.consumableManaAmount != item.consumableHealthAmount) ? (" " + item.consumableManaAmount) : "") + " Mana");
                    }
                    stringBuilder.Append("[-]");
                }
                else if (item.consumableManaAmount > 0)
                {
                    stringBuilder.Append("\n\n[00FF00]Use: Restores " + item.consumableManaAmount + " Mana[-]");
                }

                if (item.statusEffects.Count > 0)
                {
                    stringBuilder.Append("\n\n[FFFF00]Status Effects:\n");
                    for (int i = 0; i < item.statusEffects.Count; i++)
                    {
                        if (i > 0)
                        {
                            stringBuilder.Append("\n\n");
                        }
                        stringBuilder.Append(item.statusEffects[i]);
                        StatusEffect statusEffect = StatusEffectManager.Instance.Get(item.statusEffects[i]);
                        if (statusEffect != null)
                        {
                            if (statusEffect.Description.Length > 0)
                            {
                                stringBuilder.Append("\n" + statusEffect.Description);
                            }
                            else if (statusEffect.StatTypes.Count > 0)
                            {
                                stringBuilder.Append("[-]");
                                for (int x = 0; x < statusEffect.StatTypes.Count; x++)
                                {
                                    stringBuilder.Append("\n[FFFF00]" + (statusEffect.StatValues[x] >= 0 ? "+" : "-") + statusEffect.StatValues[x] + (item.quality == ItemQuality.Uncommon || isFirewood ? " " : "% ") + statusEffect.StatTypes[x].ToString() + "[-]");
                                }
                            }
                            if (statusEffect.PassiveStatTypes.Count > 0)
                            {
                                for (int x = 0; x < statusEffect.PassiveStatTypes.Count; x++)
                                {
                                    stringBuilder.Append("\n[FFFF00]" + (statusEffect.PassiveStatValues[x] > 0 ? "+" : "-") + statusEffect.PassiveStatValues[x] + (item.quality == ItemQuality.Uncommon ? " " : "% ") + ToastyTools.AddSpacesNearUpper(statusEffect.PassiveStatTypes[x].ToString()) + "[-]");
                                }
                            }
                            stringBuilder.Append("\n\n[FFFF00]Effect Duration: " + ToastyTools.SecondsToTime((int)statusEffect.Duration));
                        }
                    }
                    stringBuilder.Append("[-]");
                }
                if (item.consumableCooldown > 0f)
                {
                    stringBuilder.Append(string.Concat(new object[]
                    {
                "\nCooldown: ",
                item.consumableCooldown,
                " second",
                (item.consumableCooldown == 1f) ? "" : "s"
                    }));
                }
                if (item.requiresSitting)
                {
                    stringBuilder.Append("\nMust be sitting to consume");
                }
            }
            if (item.itemType == ItemType.Recipe)
            {
                CraftRecipe recipe = CraftingManager.Instance.GetRecipe(item.resource);
                Item item2 = ItemManager.Instance.GetItem(item.resource);
                if (item2 != null)
                {
                    stringBuilder.Append("\nLevel " + recipe.SkillLevel + " item");
                    if (item2.itemType == ItemType.Weapon || item2.itemType == ItemType.Armor)
                    {
                        stringBuilder.Append("\nRandom Attribute Chances:");
                        if (item2.IsVitalityItem())
                        {
                            stringBuilder.Append("\n| Vitality");
                        }
                        if (item2.IsDefenseItem())
                        {
                            stringBuilder.Append("\n| Defense");
                        }
                        if (item2.IsStrongItem())
                        {
                            stringBuilder.Append("\n| Strength");
                        }
                        if (item2.IsWiseItem())
                        {
                            stringBuilder.Append("\n| Wisdom");
                        }
                        if (item2.IsRangedItem())
                        {
                            stringBuilder.Append("\n| Dexterity");
                        }
                        if (item2.IsMagicItem())
                        {
                            stringBuilder.Append("\n| Magic");
                        }
                        if (item.IsCraftingItem())
                        {
                            stringBuilder.Append("\n| Crafting");
                        }
                    }
                }
                if (recipe != null && PlayerCommon.Instance && PlayerCommon.Instance.TradeSkills.RecipeIsKnown(recipe))
                {
                    stringBuilder.Append("\n\n[FFFF00]You already know how to craft this recipe[-]");
                }
                else
                {
                    stringBuilder.Append("\n\n[00FF00]Use: Teaches you how to craft " + item.resource + "[-]");
                }

                Item _item = ItemManager.Instance.GetItem(item.resource);
                stringBuilder.Append("\n\n" + GetCustomItemTooltipText(_item, 1, false, false, false, false, false, false, false, false, true));
            }
            if (item.itemType == ItemType.Scroll)
            {
                stringBuilder.Append(string.Concat(new object[]
                {
            "\n",
            ToastyTools.AddSpacesNearUpper(item.scrollType.ToString()),
            " ",
            item.itemType
                }));
                if (item.scrollType == ScrollType.Experience)
                {
                    if (uint.TryParse(item.resource, out uint num))
                    {
                        stringBuilder.Append("\n\n[00FF00]Use: Grants " + num.ToString("N0") + " XP");
                    }
                }
                else if (item.scrollType == ScrollType.LevelUp)
                {
                    stringBuilder.Append("\n\n[00FF00]Use: Increases your level by one");
                }
                else if (item.scrollType == ScrollType.Level)
                {
                    if (int.TryParse(item.resource, out int num2))
                    {
                        stringBuilder.Append("\n\n[00FF00]Use: Sets your level to " + num2);
                    }
                }
                else if (item.scrollType == ScrollType.Armor)
                {
                    stringBuilder.Append(string.Concat(new object[]
                    {
                "\n\n[00FF00]Use: Gives you a random piece of level ",
                PlayerCommon.Instance.Stats.CurrentLevel,
                " ",
                item.quality,
                " Armor[-]"
                    }));
                }
                else if (item.scrollType == ScrollType.Weapon)
                {
                    stringBuilder.Append(string.Concat(new object[]
                    {
                "\n\n[00FF00]Use: Gives you a random level ",
                PlayerCommon.Instance.Stats.CurrentLevel,
                " ",
                item.quality,
                " Weapon[-]"
                    }));
                }
                else if (item.scrollType == ScrollType.Gold)
                {
                    if (int.TryParse(item.resource, out int num3))
                    {
                        stringBuilder.Append("\n\n[00FF00]Use: Gives you [FFD800]" + num3.ToString("N0") + " Gold[-][-]");
                    }
                }
                else if (item.scrollType == ScrollType.StatReset)
                {
                    stringBuilder.Append("\n\n[00FF00]Use: Resets your Stats and Skills[-]");
                }
                else if (item.scrollType == ScrollType.Rename)
                {
                    stringBuilder.Append("\n\n[00FF00]Use: Renames your character[-]");
                }
                else if (item.scrollType == ScrollType.Emote)
                {
                    string str = item.baseName.Split(new char[]
                    {
                ':'
                    })[1].Trim();
                    stringBuilder.Append("\n\n[00FF00]When purchased, teaches you how to use the " + str + " emote");
                }
                else if (item.scrollType == ScrollType.Redesign)
                {
                    stringBuilder.Append("\n\n[00FF00]Use: Allows you to re-design your character's appearance[-]");
                }
                else if (item.scrollType == ScrollType.Disenchant)
                {
                    stringBuilder.Append("\n\n[00FF00]Use: Break down an equipment item into materials usable by enchanters[-]");
                }
                else if (item.scrollType == ScrollType.Repair)
                {
                    stringBuilder.Append("\n\n[00FF00]Use: Select a broken equipment item to repair[-]");
                }
                else if (item.scrollType == ScrollType.IncreaseItemQuality)
                {
                    stringBuilder.Append("\n\n[00FF00]Use: Select an equipment item to attempt to increase the quality[-]");
                    if (isInCraftWindow)
                    {
                        CraftRecipe recipe2 = CraftingManager.Instance.GetRecipe("Recipe: " + item.baseName);
                        if (recipe2 != null)
                        {
                            stringBuilder.Append("\nUsable on equipment below level " + (recipe2.SkillLevel + 1));
                        }
                    }
                    else
                    {
                        stringBuilder.Append("\nUsable on equipment below level " + (item.ItemLevel + 1));
                    }
                    if (item.quality < ItemQuality.Epic)
                    {
                        stringBuilder.Append("\n[FFFF00]Selected item will break on failure (repairable)[-]");
                        if (item.quality == ItemQuality.Rare)
                        {
                            stringBuilder.Append(string.Concat(new object[]
                            {
                        "\n\nSuccess Rates:\n+1 - +3: 100%\n+4 - +5: ",
                        Mathf.RoundToInt((CraftingManager.Instance.enchantChanceMid + CraftingManager.Instance.enchantRareBonus) * 100f),
                        "%\n+6 - +7: ",
                        Mathf.RoundToInt((CraftingManager.Instance.enchantChanceHigh + CraftingManager.Instance.enchantRareBonus) * 100f),
                        "%"
                            }));
                        }
                        else
                        {
                            stringBuilder.Append(string.Concat(new object[]
                            {
                        "\n\nSuccess Rates:\n+1 - +3: 100%\n+4 - +5: ",
                        Mathf.RoundToInt(CraftingManager.Instance.enchantChanceMid * 100f),
                        "%\n+6 - +7: ",
                        Mathf.RoundToInt(CraftingManager.Instance.enchantChanceHigh * 100f),
                        "%"
                            }));
                        }
                    }
                    else
                    {
                        stringBuilder.Append("\n\nSuccess Rate: 100%");
                    }
                }
                else if (item.scrollType == ScrollType.IncreaseItemStats)
                {
                    stringBuilder.Append("\n\n[00FF00]Use: Select an equipment item to attempt to increase the stats[-]");
                    if (isInCraftWindow)
                    {
                        CraftRecipe recipe3 = CraftingManager.Instance.GetRecipe("Recipe: " + item.baseName);
                        if (recipe3 != null)
                        {
                            stringBuilder.Append("\nUsable on equipment below level " + (recipe3.SkillLevel + 1));
                        }
                    }
                    else
                    {
                        stringBuilder.Append("\nUsable on equipment below level " + (item.ItemLevel + 1));
                    }
                    if (item.quality < ItemQuality.Epic)
                    {
                        stringBuilder.Append("\n[FFFF00]Selected item will break on failure (repairable)[-]");
                        if (item.quality == ItemQuality.Rare)
                        {
                            stringBuilder.Append(string.Concat(new object[]
                            {
                        "\n\nSuccess Rates:\nNormal: 100%\nShiny: ",
                        Mathf.RoundToInt((CraftingManager.Instance.enchantChanceMid + CraftingManager.Instance.enchantRareBonus) * 100f),
                        "%\nRadiant: ",
                        Mathf.RoundToInt((CraftingManager.Instance.enchantChanceHigh + CraftingManager.Instance.enchantRareBonus) * 100f),
                        "%"
                            }));
                        }
                        else
                        {
                            stringBuilder.Append(string.Concat(new object[]
                            {
                        "\n\nSuccess Rates:\nNormal: 100%\nShiny: ",
                        Mathf.RoundToInt(CraftingManager.Instance.enchantChanceMid * 100f),
                        "%\nRadiant: ",
                        Mathf.RoundToInt(CraftingManager.Instance.enchantChanceHigh * 100f),
                        "%"
                            }));
                        }
                    }
                    else
                    {
                        stringBuilder.Append("\n\nSuccess Rate: 100%");
                    }
                }
            }
            if (item.itemType == ItemType.Spawner && item.resource != "RANDOM")
            {
                stringBuilder.Append("\n\n[00FF00]Use: Spawns a " + item.spawnedName + " next to you[-]");

                isFirewood = false;

                switch (item.id)
                {
                    case 2562: item.statusEffects.Add("Warmed Up"); isFirewood = true; break; // Oak Firewood
                    case 2564: item.statusEffects.Add("Snug"); isFirewood = true; break; // Walnut Firewood
                    case 2565: item.statusEffects.Add("Comfy"); isFirewood = true; break; // Hickory Firewood
                    case 2566: item.statusEffects.Add("Cozy"); isFirewood = true; break; // Mahogany Firewood
                    case 2567: item.statusEffects.Add("Relaxed"); isFirewood = true; break; // Redheart Firewood
                    case 2568: item.statusEffects.Add("Peaceful"); isFirewood = true; break; // Birch Firewood
                    case 2569: item.statusEffects.Add("Serene"); isFirewood = true; break; // Pine Firewood
                    case 2570: item.statusEffects.Add("Tranquil"); isFirewood = true; break; // Ash Firewood
                }

                if (isFirewood)
                {
                    stringBuilder.Append("\n\n[FFFF00]Status Effects:\n");

                    StatusEffect statusEffect = StatusEffectManager.Instance.Get(item.statusEffects[0]);

                    if (statusEffect.StatTypes.Count > 0)
                    {
                        for (int x = 0; x < statusEffect.StatTypes.Count; x++)
                        {
                            stringBuilder.Append("\n[FFFF00]" + (statusEffect.StatValues[x] > 0 ? "+" : "-") + statusEffect.StatValues[x] + "% " + ToastyTools.AddSpacesNearUpper(statusEffect.StatTypes[x].ToString()) + "[-]");
                        }
                    }

                    if (statusEffect.PassiveStatTypes.Count > 0)
                    {
                        for (int x = 0; x < statusEffect.PassiveStatTypes.Count; x++)
                        {
                            stringBuilder.Append("\n[FFFF00]" + (statusEffect.PassiveStatValues[x] > 0 ? "+" : "-") + statusEffect.PassiveStatValues[x] + "% " + ToastyTools.AddSpacesNearUpper(statusEffect.PassiveStatTypes[x].ToString()) + "[-]");
                        }
                    }
                    stringBuilder.Append("\n\n[FFFF00]Effect Duration: " + ToastyTools.SecondsToTime((int)statusEffect.Duration));

                    stringBuilder.Append("[-]");
                }

                if (item.spawnedLocation.Length > 0)
                {
                    stringBuilder.Append("\nMust be at '" + item.spawnedLocation + "' to summon");
                }
            }
            if (item.itemType == ItemType.Mount)
            {
                stringBuilder.Append(string.Concat(new object[]
                {
            "\n",
            item.mountType,
            " ",
            item.itemType
                }));
                if (item.speed != 0f)
                {
                    if (item.speed > 0f)
                    {
                        stringBuilder.Append("\nIncreases");
                    }
                    else
                    {
                        stringBuilder.Append("\nDecreases");
                    }
                    stringBuilder.Append(" movement speed by " + Mathf.Abs(item.speed) + "% while mounted");
                }
                stringBuilder.Append("\n\n[00FF00]Use: Mount or Dismount");
            }
            if (item.itemType == ItemType.Pet)
            {
                stringBuilder.Append("\n" + item.itemType);
                stringBuilder.Append("\n\n[00FF00]Use: Summon/Dismiss Pet");
            }
            if (item.itemType == ItemType.CraftingMaterial)
            {
                stringBuilder.Append("\nCrafting Material");
            }
            if (item.itemType == ItemType.Event)
            {
                stringBuilder.Append("\n" + item.itemType + " Item");
            }
            foreach (KeyValuePair<StatType, int> keyValuePair in item.Stats)
            {
                stringBuilder.Append("\n+");
                bool flag = item.craftQuality > 0 && keyValuePair.Key == (StatType)(item.attribute - 1);
                if (flag)
                {
                    stringBuilder.Append("[00FF00]");
                }
                stringBuilder.Append(keyValuePair.Value + (flag ? item.craftQuality : 0));
                if (flag)
                {
                    stringBuilder.Append("[-]");
                }
                stringBuilder.Append(" " + keyValuePair.Key);
            }
            if (item.CraftingStat > 0)
            {
                stringBuilder.Append("\n+");
                if (item.craftQuality > 0)
                {
                    stringBuilder.Append("[00FF00]");
                }
                stringBuilder.Append(item.CraftingStat + item.craftQuality);
                if (item.craftQuality > 0)
                {
                    stringBuilder.Append("[-]");
                }
                stringBuilder.Append(" Crafting");
            }
            if (item.passiveEffects.Count > 0)
            {
                stringBuilder.Append("\n\n[00FF00]");
                for (int j = 0; j < item.passiveEffects.Count; j++)
                {
                    if (j > 0)
                    {
                        stringBuilder.Append("\n");
                    }
                    stringBuilder.Append(string.Concat(new object[]
                    {
                "+",
                item.passiveEffectValues[j],
                "% ",
                ToastyTools.AddSpacesNearUpper(item.passiveEffects[j].ToString())
                    }));
                }
                stringBuilder.Append("[-]");
            }
            if (item.itemType == ItemType.Projectile)
            {
                stringBuilder.Append("\n+" + item.ProjectileDamage + " Ranged Damage");
            }
            if (item.combatLevelRequirement > 1 || item.TradeLevelRequirement > 1 || item.StatRequirements.Count > 0 || (item.itemType == ItemType.Weapon && item.weaponType == WeaponType.Ranged))
            {
                stringBuilder.Append("\n\nRequires:");
                if (item.combatLevelRequirement > 1)
                {
                    bool flag2 = PlayerCommon.Instance.Stats.CurrentLevel < item.combatLevelRequirement;
                    if (flag2)
                    {
                        stringBuilder.Append("[FF0000]");
                    }
                    stringBuilder.Append("\nLevel " + item.combatLevelRequirement);
                    if (flag2)
                    {
                        stringBuilder.Append("[-]");
                    }
                }
                if (item.IsTradeTool() && item.TradeLevelRequirement > 1)
                {
                    bool flag3 = PlayerCommon.Instance.TradeSkills.Level(item.TradeSkillRequirement) < item.TradeLevelRequirement;
                    if (flag3)
                    {
                        stringBuilder.Append("[FF0000]");
                    }
                    stringBuilder.Append(string.Concat(new object[]
                    {
                "\n",
                item.TradeSkillRequirement,
                " Level ",
                item.TradeLevelRequirement
                    }));
                    if (flag3)
                    {
                        stringBuilder.Append("[-]");
                    }
                }
            }
            foreach (KeyValuePair<StatType, int> keyValuePair2 in item.StatRequirements)
            {
                stringBuilder.Append(string.Concat(new object[]
                {
            "\n",
            keyValuePair2.Key,
            ": ",
            keyValuePair2.Value
                }));
            }
            if (item.itemSet.Length > 0)
            {
                List<Item> itemSet = ItemManager.Instance.GetItemSet(item.itemSet);
                stringBuilder.Append("\n\n" + text + item.itemSet + "[-]");
                foreach (Item item3 in itemSet)
                {
                    if (item3.baseName == item.baseName)
                    {
                        stringBuilder.Append("\n " + text + item3.baseName + "[-]");
                    }
                    else
                    {
                        stringBuilder.Append("\n " + item3.baseName);
                    }
                }
            }
            if (item.description.Length > 0)
            {
                stringBuilder.Append("\n\n[i][00FF00]\"" + item.description + "\"[-][/i]");
            }
            if (isVendor && !isInline)
            {
                if (multiple)
                {
                    stringBuilder.Append("\n\n[00FF00]<Right Click to Purchase One>\n<Shift + Right Click to Purchase Bulk>[-]");
                }
                else
                {
                    stringBuilder.Append("\n\n[00FF00]<Right Click to Purchase>[-]");
                }
            }
            else if (item.isSellable && !isInline)
            {
                stringBuilder.Append(string.Concat(new object[]
                {
            "\n\n[00FF00]Sell Price: [FFD800]",
            item.SellPrice * stackCount,
            "G[-]",
            (stackCount > 1) ? (" ([FFD800]" + item.SellPrice + "G[-] each)\n<Shift-Click to split stack>") : "",
            "[-]"
                }));
            }
            if (!isInCraftWindow && !isInChatWindow && !isInline)
            {
                stringBuilder.Append("\n[00FF00]<CTRL-Click to link to chat>[-]");
            }
            if ((item.itemType == ItemType.Armor || item.itemType == ItemType.Weapon) && !isEquipped && !isInline)
            {
                stringBuilder.Append("\n[00FF00]<Alt-Click to view>[-]");
            }
            if (!item.usableInCombat && !isInline)
            {
                stringBuilder.Append("\n[666666]<Cannot be used while in combat>[-]");
            }
            if (!item.isTradable && !isInline)
            {
                stringBuilder.Append("\n[666666]<Cannot be traded>[-]");
            }
            if (!item.isDroppable && !isInline)
            {
                stringBuilder.Append("\n[666666]<Cannot be dropped");
                if (!item.isDestroyable)
                {
                    stringBuilder.Append(" or destroyed");
                }
                stringBuilder.Append(">[-]");
            }
            if (!item.isSellable && !isInline)
            {
                stringBuilder.Append("\n[666666]<Cannot be sold>[-]");
            }
            if (item.dungeonOnly && !isInline)
            {
                stringBuilder.Append("\n[666666]<Dungeon Only>[-]");
            }
            if (item.isGameMasterItem && !isInline)
            {
                stringBuilder.Append("\n[FF8000]<Can only be used by GMs>[-]");
            }
            if (isSelling && item.isSellable && !isInline)
            {
                stringBuilder.Append("\n[00FF00]<Right Click to Sell>[-]");
            }
            if (isTrading && item.isTradable && !isInline)
            {
                stringBuilder.Append("\n[00FF00]<Right Click to Trade>[-]");
            }
            else if (isInTradeWindow && !isInline)
            {
                stringBuilder.Append("\n[00FF00]<Right Click to Remove>[-]");
            }
            if (PlayerCommon.Instance && !isInline && PlayerCommon.Instance.Stats.isGameMaster)
            {
                stringBuilder.Append("\n\nDEBUG:");
                stringBuilder.Append("\nID: " + item.id);
                stringBuilder.Append("\niLvl: " + item.Level);
                stringBuilder.Append("\nSeed: " + item.seed);
            }
            return stringBuilder.ToString();
        }

        #endregion Adds crafted item to crafting recipe tooltip, also adds actual effect to potions tooltip

        #region Add game wallet balance to HPB wallet panel

        private void OnHpbManagerGetBalance(On.HpbManager.orig_GetBalance orig, HpbManager self, decimal balance)
        {
            GuiManager.Instance.panelGame.containerHPB.walletBalance.text = $"Your Wallet: {balance} - Game Wallet: {currentGameBalance}";
            GuiManager.Instance.panelGame.containerHPB.walletBalanceDecimal = balance;
        }

        private void OnContainerHPBShow(On.ContainerHPB.orig_Show orig, ContainerHPB self)
        {
            self.walletAddress.text = UserManager.Instance.PublicKey;
            self.inputWalletAddress.value = UserManager.Instance.PublicKey;
            self.inputWalletAddress.savedAs = UserManager.Instance.PublicKey;
            Texture2D mainTexture = self.generateQR(UserManager.Instance.PublicKey);
            self.qrWalletTexture.mainTexture = mainTexture;
            HpbManager.Instance.repeatBalanceQuery = true;
            repeatBalanceQuery = true;
            self.transferDestination.SetDirty();
            self.transferAmount.SetDirty();
            HpbManager.Instance.AskForBalance(false);
            StartCoroutine(CheckGameWalletBalance());
            NGUITools.SetActive(base.gameObject, true);
        }

        private IEnumerator CheckGameWalletBalance()
        {
            string _node = "https://node.myhpbwallet.com";
            string address = "0x7275d6f7103f897dcf5c8a0a292141fb648458f5";

            EthGetBalanceUnityRequest balanceRequest = new EthGetBalanceUnityRequest(_node, null);
            yield return balanceRequest.SendRequest(address, BlockParameter.CreateLatest());
            currentGameBalance = UnitConversion.Convert.FromWei(balanceRequest.Result.Value, UnitConversion.EthUnit.Ether);

            float time = 1f;
            while (repeatBalanceQuery)
            {
                yield return new WaitForSeconds(time);
                yield return balanceRequest.SendRequest(address, BlockParameter.CreateLatest());
                currentGameBalance = UnitConversion.Convert.FromWei(balanceRequest.Result.Value, UnitConversion.EthUnit.Ether);

                UpdateGameWalletBalance();
            }

            yield break;
        }

        private void UpdateGameWalletBalance()
        {
            decimal _playerBalance = GuiManager.Instance.panelGame.containerHPB.walletBalanceDecimal;
            GuiManager.Instance.panelGame.containerHPB.walletBalance.text = $"Your Wallet: {_playerBalance} - Game Wallet: {currentGameBalance}";
        }

        private void OnContainerHPBHide(On.ContainerHPB.orig_Hide orig, ContainerHPB self)
        {
            orig.Invoke(self);
            repeatBalanceQuery = false;
        }

        #endregion Add game wallet balance to HPB wallet panel

        #endregion Quality of Life Fixes
    }
}