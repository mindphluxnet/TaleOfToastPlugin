using BepInEx;
using System;
using UnityEngine;
using BepInEx.Configuration;
using System.Collections.Generic;
using System.Collections;
using uLink;

namespace TheTaleOfToastPlugin
{
    [BepInPlugin("net.mindphlux.plugins.thetaleoftoastplugin", "The Tale of Toast Plugin", "1.0.4")]
    [BepInProcess("ToT.exe")]
    public class TheTaleOfToastPlugin: BaseUnityPlugin
    {
        private ConfigEntry<int> pinnedTradeSkill;
        private ConfigEntry<int> unknownLevelThreshold;
        private ConfigEntry<int> lastSummonedPet;

        private float onDisplayLocationCounter;
        private bool canLogin;
        private Item dressingRoomItem;
        private bool mailboxFixed = false;
        private bool bagSpaceLabelAdded = false;
        private UILabel bagSpaceLabel;
        private bool discordInitialized = false;
        private DiscordRpc.RichPresence _richPresence;

        void Awake()
        {
            unknownLevelThreshold = Config.Bind("General", "UnknownLevelThreshold", 10, "Set the threshold for displaying ?? instead of a level.");
            pinnedTradeSkill = Config.Bind("DoNotChange", "PinnedTradeSkill", -1, "The currently pinned trade skill (because the game doesn't save it)");
            lastSummonedPet = Config.Bind("DoNotChange", "LastSummonedPet", -1, "The last summoned vanity pet");

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
            On.PlayerInventory.AddItem += OnPlayerInventoryAddItem;
            On.PlayerInventory.RemoveItem += OnPlayerInventoryRemoveItem;
            On.UIItemCursor.EquipVanityItem += OnUIItemCursorEquipVanityItem;
            On.UIItemCursor.UnequipVanityItem += OnUIItemCursorUnequipVanityItem;
            On.UIItemCursor.EquipItem += OnUIItemCursorEquipItem;
            On.UIItemCursor.UnequipItem += OnUIItemCursorUnequipItem;
            On.DiscordController.UpdateDiscord += OnDiscordControllerUpdateDiscord;
            On.PlayerStats.LeveledUp += OnPlayerStatsLeveledUp;
        }

        void Update()
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
                DiscordRpc.UpdatePresence(ref _richPresence);
                discordInitialized = true;
            }

            ResetMovementLogoutTimer();
            FixMinimapIcons();

            if(!bagSpaceLabelAdded)
            {
                bagSpaceLabelAdded = true;
                StartCoroutine(BagSlotsOnButtonMenu());
            }

            if(Input.GetKeyDown(KeyCode.F2))
            {
                SettingsManager.Instance.ToggleLockActionBars();
                bool _locked = SettingsManager.Instance.LockActionBars;
                string _msg = "locked.";
                if (!_locked) _msg = "unlocked.";
                Chat.Instance.InfoMessage(ChatMessageType.Info, $"Action bars are now {_msg}");
            }
        }

        #region Quality of Life Fixes

        private void OnPlayerStatsLeveledUp(On.PlayerStats.orig_LeveledUp orig, PlayerStats self, int level, int availablePoints, int pointsGained)
        {
            orig.Invoke(self, level, availablePoints, pointsGained);
            CustomUpdateDiscord(level.ToString());
        }

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

        IEnumerator BagSlotsOnButtonMenu()
        {
            yield return new WaitForSeconds(3f);

            GameObject _go = FindObjectOfType<ContainerButtonMenu>().gameObject;
            UILabel[] _label = _go.GetComponentsInChildren<UILabel>();

            UILabel _labelToClone = null;

            for(int i = 0; i < _label.Length; i++)
            {
                if (_label[i].name == "Label_Diamonds")
                {
                    _labelToClone = _label[i];
                }
            }

            if(_labelToClone != null)
            {
                Transform _parentTransform = null;

                UISprite[] _sprites = _go.GetComponentsInChildren<UISprite>();

                for(int i = 0; i < _sprites.Length; i++)
                {
                    if(_sprites[i].name == "Button_ToggleMenu")
                    {
                        _parentTransform = _sprites[i].transform;
                    }
                }

                if(_parentTransform != null)
                {
                    bagSpaceLabel = Instantiate(_labelToClone, _go.transform);
                    bagSpaceLabel.transform.localPosition = new Vector2(-112f, -73f);
                    UpdateBagSpaceLabel();
                }


            }
        }

        private void UpdateBagSpaceLabel()
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

        private void MailBoxBackButtonFix()
        {
            GuiManager.Instance.panelGame.containerMail.SetTab(MailTab.List);
        }

        private void OnContainerTradeSkillsShowRecipe(On.ContainerTradeSkills.orig_ShowRecipe orig, ContainerTradeSkills self, string recipeName)
        {
            // on the original Trade Skills window it isn't possible to view the crafted item in the dressing room
            // despite the tooltip claiming Alt+Click would preview it. 
            // to make this work we'll add an UIButton component to the item icon and route the onClick handler to our custom function

            orig.Invoke(self, recipeName);

            dressingRoomItem = ItemManager.Instance.GetItem(recipeName);

            if (self.recipeIcon.gameObject.GetComponent<UIButton>()) return;
            
            UIButton _button = self.recipeIcon.gameObject.AddComponent<UIButton>();
            EventDelegate DessingRoomPreview = new EventDelegate(this, "ShowDressingRoom");
            _button.onClick.Add(DessingRoomPreview);
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

        private void OnPanelLoginStart(On.PanelLogin.orig_Start orig, PanelLogin self)
        {
            canLogin = false;
        }

        private void OnRealmSelected(On.PanelLogin.orig_OnRealmSelected orig, PanelLogin self)
        {
            orig.Invoke(self);
            if (UserManager.Instance.Realm != "") canLogin = true;
        }

        private void PinnedTradeskillXpUpdate(On.ContainerPinnedTradeskill.orig_XpUpdate orig, ContainerPinnedTradeskill self, TradeSkill skill, int level, uint levelXp, uint nextLevelXp, uint totalXp)
        {
     
            if (skill == (TradeSkill)pinnedTradeSkill.Value)
            {
                UILabel _label = GuiManager.Instance.panelGame.containerPinnedTradeskill.GetComponentInChildren<UILabel>();
                _label.text = ((TradeSkill)pinnedTradeSkill.Value).ToString() + " " + level;
                
                orig.Invoke(self, skill, level, levelXp, nextLevelXp, totalXp);
            }
        }

        private void LevelConFix(On.ContainerTargetInfo.orig_Set_bool_bool_int_string_int_int_int_int_int_BaseStats_float_int orig, ContainerTargetInfo self, bool enabled, bool attackable, int playerLevel, string targetName, int targetLevel, int targetHealth, int targetMaxHealth, int targetMana, int targetMaxMana, BaseStats targetStats, float distance, int threat)
        {
            // set the threshold for displaying ?? as level to an user defined value instead of 6
            // defaults to 10 levels like in most games

            GameManager.Instance.unknownLevelThreshold = unknownLevelThreshold.Value;
            orig.Invoke(self, enabled, attackable, playerLevel, targetName, targetLevel, targetHealth, targetMaxHealth, targetMana, targetMaxMana, targetStats, distance, threat);
        }
        private void OnSetTarget(On.PlayerTargeting.orig_SetTarget orig, PlayerTargeting self, ITargetable newTarget)
        {
            // fixes the problem that right-clicking the player targets them
            // which can cause issues in combat and is also rather irritating

            if (newTarget.TargetName == PlayerCommon.Instance.Init.PlayerName) return;
            
            orig.Invoke(self, newTarget);
        }
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
        private void SavePinnedTradeskill(On.ContainerTradeSkills.orig_PinSelectedTradeskill orig, ContainerTradeSkills self)
        {
            // saves the currently pinned trade skill to the plugin's config file
            
            orig.Invoke(self);
            TradeSkill _tradeskill = GuiManager.Instance.panelGame.containerPinnedTradeskill.Skill;
            pinnedTradeSkill.Value = (int)_tradeskill;
        }
        IEnumerator RestorePinnedTradeSkill()
        {
            // restores the pinned trade skill from the plugin's config file
            // for safety reasons we defer the execution by 5 seconds to make sure all values are actually initialized

            yield return new WaitForSeconds(5f);

            TradeSkill _tradeSkill = (TradeSkill)pinnedTradeSkill.Value;

            int _level = PlayerCommon.Instance.TradeSkills.Level(_tradeSkill);
            uint _xpNow = PlayerCommon.Instance.TradeSkills.Experience[_tradeSkill] - XpManager.Instance.ConvertLevelToXp(PlayerCommon.Instance.TradeSkills.Level(_tradeSkill));
            uint _xpNext = XpManager.Instance.ConvertLevelToXp(PlayerCommon.Instance.TradeSkills.Level(_tradeSkill) + 1);
            uint _totalNow = PlayerCommon.Instance.TradeSkills.Experience[_tradeSkill];

            GuiManager.Instance.panelGame.containerPinnedTradeskill.Set((TradeSkill)pinnedTradeSkill.Value, _level, _xpNow, (_xpNext - _totalNow) + _xpNow, 0);
            GuiManager.Instance.panelGame.containerPinnedTradeskill.gameObject.SetActive(true);
        }

        private void FixMinimapIcons()
        {
            // fixes the way too large minimap icons on the highest zoom level

            GameObject[] _icons = GameObject.FindObjectsOfType<GameObject>();
            for(int i = 0; i < _icons.Length; i++)
            {
                
                if(_icons[i].GetComponent<CraftStation>() ||
                    _icons[i].GetComponent<NpcCommon>() ||
                    _icons[i].GetComponent<Mailbox>() ||
                    _icons[i].GetComponent<LootableObject>()
                )
                {
                    if (!_icons[i].GetComponent<PhatRobit.MiniMapIcon>()) continue;
                    if (_icons[i].GetComponent<NpcCommon>() && !_icons[i].GetComponent<NpcVendor>().isEnabled) continue;
                 
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

        private void PressEnterToEnterWorld()
        {
            // adds the ability to enter the world by pressing Return or Enter on the character selection screen
            // like World of Warcraft does it

            if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
            {
                GuiManager.Instance.panelCharacterSelection.EnterWorld();
            }
        }
        private void PressEnterToLogin()
        {
            // adds the ability to login by pressing Return or Enter on the login screen

            if (!canLogin) return;

            if(Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
            {
                SteamManager.Instance.TryLogin();
            }
        }
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

            if(enabled)
            {
                StartCoroutine(ResummonPetAfterFlying(self));
            }
        }

        IEnumerator ResummonPetAfterFlying(PlayerMovement _movement)
        {
            // automatically resummons the last summoned pet after completing a flight path

            while(_movement.isOnFlightPath)
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

            if(lastSummonedPet.Value != -1)
            {
                PlayerCommon.Instance.Pets.SendSummonRequest(lastSummonedPet.Value);
            }
        }

        private void CustomUpdateDiscord(string playerLevel, string playerName = null)
        {
            if(playerName == null)
            {
                playerName = PlayerCommon.Instance.Init.PlayerName;
            }

            if (discordInitialized)
            {
                _richPresence.largeImageKey = "toastlogo";
                _richPresence.smallImageKey = "toastlogo";
                _richPresence.state = $"Adventuring in Astaria";
                _richPresence.details = $"{playerName}, level {playerLevel}";
                DiscordRpc.UpdatePresence(ref _richPresence);
            }
        }
        private void OnDiscordControllerUpdateDiscord(On.DiscordController.orig_UpdateDiscord orig, DiscordController self, string largeImage, string realm, string smallImage, string playerName, string playerLevel)
        {
            CustomUpdateDiscord(playerLevel, playerName);
        }

        #endregion

    }
}
