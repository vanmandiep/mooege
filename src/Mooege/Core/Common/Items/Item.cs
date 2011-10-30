﻿/*
 * Copyright (C) 2011 mooege project
 *
 * This program is free software; you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation; either version 2 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with this program; if not, write to the Free Software
 * Foundation, Inc., 59 Temple Place, Suite 330, Boston, MA  02111-1307  USA
 */

using System.Collections.Generic;
using System.Reflection;
using Mooege.Common;
using Mooege.Core.GS.Actors;
using Mooege.Core.GS.Common.Types.Math;
using Mooege.Core.GS.Map;
using Mooege.Core.Common.Items.ItemCreation;
using Mooege.Net.GS.Message.Definitions.World;
using Mooege.Net.GS.Message.Definitions.Misc;
using Mooege.Net.GS.Message.Fields;
using Mooege.Net.GS.Message.Definitions.Effect;
using Mooege.Net.GS.Message;
using Mooege.Common.MPQ.FileFormats;
using Mooege.Common.Helpers;
using Mooege.Core.Common.Scripting;

// TODO: This entire namespace belongs in GS. Bnet only needs a certain representation of items whereas nearly everything here is GS-specific

namespace Mooege.Core.Common.Items
{
    /*
    public enum ItemType
    {
        Unknown, Helm, Gloves, Boots, Belt, Shoulders, Pants, Bracers, Shield, Quiver, Orb,
        Axe_1H, Axe_2H, CombatStaff_2H, Staff, Dagger, Mace_1H, Mace_2H, Sword_1H,
        Sword_2H, Crossbow, Bow, Spear, Polearm, Wand, Ring, FistWeapon_1H, ThrownWeapon, ThrowingAxe, ChestArmor,
        HealthPotion, Gold, HealthGlobe, Dye, Elixir, Charm, Scroll, SpellRune, Rune,
        Amethyst, Emarald, Ruby, Emerald, Topaz, Skull, Backpack, Potion, Amulet, Scepter, Rod, Journal

        // Not working at the moment:
        // ThrownWeapon, ThrowingAxe - does not work because there are no snoId in Actors.txt. Do they actually drop in the D3 beta? /angerwin?
        // Diamond, Sapphire - I realised some days ago, that the Item type Diamond and Shappire (maybe not the only one) causes client crash and BAD GBID messages, although they actually have SNO IDs. /angerwin
    }
    */
    public class Item : Mooege.Core.GS.Actors.Actor
    {
        private static readonly Logger Logger = LogManager.CreateLogger();

        public override ActorType ActorType { get { return ActorType.Item; } }

        public GS.Player.Player Owner { get; set; } // Only set when the player has the item in its inventory. /komiga

        public ItemTypeTable ItemType { get; set; }

        public ItemRandomHelper RandomGenerator { get; private set; }
        public int ItemLevel { get; private set; }


        public int EquipmentSlot { get; private set; }
        public Vector2D InventoryLocation { get; private set; } // Column, row; NOTE: Call SetInventoryLocation() instead of setting fields on this

        public override bool HasWorldLocation
        {
            get { return this.Owner == null; }
        }

        public override InventoryLocationMessageData InventoryLocationMessage
        {
            get
            {
                return new InventoryLocationMessageData
                {
                    OwnerID = (this.Owner != null) ? this.Owner.DynamicID : 0,
                    EquipmentSlot = this.EquipmentSlot,
                    InventoryLocation = this.InventoryLocation
                };
            }
        }

        public InvLoc InvLoc
        {
            get
            {
                return new InvLoc
                {
                    OwnerID = (this.Owner != null) ? this.Owner.DynamicID : 0,
                    EquipmentSlot = this.EquipmentSlot,
                    Row = this.InventoryLocation.Y,
                    Column = this.InventoryLocation.X
                };
            }
        }

        public Item(Mooege.Core.GS.Map.World world, ItemTable definition)
            : base(world, world.NewActorID)
        {
            this.ActorSNO = definition.SNOActor;
            this.GBHandle.Type = (int)GBHandleType.Gizmo;
            this.GBHandle.GBID = definition.Hash;
            this.ItemType = ItemGroup.FromHash(definition.ItemType1);
            this.EquipmentSlot = 0;
            this.InventoryLocation = new Vector2D { X = 0, Y = 0 };

            this.Field2 = 0x00000000;
            this.Field3 = 0x00000000;
            this.Field7 = 0;
            this.Field8 = 0;
            this.Field9 = 0x00000000;
            this.Field10 = 0x00;

            this.ItemLevel = definition.ItemLevel;

            // level requirement
            // Attributes[GameAttribute.Requirement, 38] = definition.RequiredLevel;

            if (definition.Quality == ItemTable.ItemQuality.Invalid)
                Attributes[GameAttribute.Item_Quality_Level] = RandomHelper.Next(6);

            Attributes[GameAttribute.Seed] = RandomHelper.Next(); //unchecked((int)2286800181);

            /*
            List<IItemAttributeCreator> attributeCreators = new AttributeCreatorFactory().Create(this.ItemType);
            foreach (IItemAttributeCreator creator in attributeCreators)
            {
                creator.CreateAttributes(this);
            }
            */

            RandomGenerator = new ItemRandomHelper(Attributes[GameAttribute.Seed]);
            RandomGenerator.Next();
            if (Item.IsArmor(this.ItemType))
                RandomGenerator.Next(); // next value is used but unknown if armor
            RandomGenerator.ReinitSeed();

            ApplyWeaponSpecificOptions(definition);
            ApplyArmorSpecificOptions(definition);
            ApplyDurability(definition);
            ApplySkills(definition);
            ApplyAttributeSpecifier(definition);

            int affixNumber = 1;
            if (Attributes[GameAttribute.Item_Quality_Level] >= 3)
                affixNumber = Attributes[GameAttribute.Item_Quality_Level] - 2;
            AffixGenerator.Generate(this, affixNumber);

            this.World.Enter(this); // Enter only once all fields have been initialized to prevent a run condition
        }

        private void ApplyWeaponSpecificOptions(ItemTable definition)
        {
            if (definition.WeaponDamageMin > 0)
            {
                Attributes[GameAttribute.Attacks_Per_Second_Item] += definition.AttacksPerSecond;
                Attributes[GameAttribute.Attacks_Per_Second_Item_Subtotal] += definition.AttacksPerSecond;
                Attributes[GameAttribute.Attacks_Per_Second_Item_Total] += definition.AttacksPerSecond;

                Attributes[GameAttribute.Damage_Weapon_Min, 0] += definition.WeaponDamageMin;
                Attributes[GameAttribute.Damage_Weapon_Min_Total, 0] += definition.WeaponDamageMin;

                Attributes[GameAttribute.Damage_Weapon_Delta, 0] += definition.WeaponDamageDelta;
                Attributes[GameAttribute.Damage_Weapon_Delta_SubTotal, 0] += definition.WeaponDamageDelta;
                Attributes[GameAttribute.Damage_Weapon_Delta_Total, 0] += definition.WeaponDamageDelta;

                Attributes[GameAttribute.Damage_Weapon_Max, 0] += Attributes[GameAttribute.Damage_Weapon_Min, 0] + Attributes[GameAttribute.Damage_Weapon_Delta, 0];
                Attributes[GameAttribute.Damage_Weapon_Max_Total, 0] += Attributes[GameAttribute.Damage_Weapon_Min_Total, 0] + Attributes[GameAttribute.Damage_Weapon_Delta_Total, 0];

                Attributes[GameAttribute.Damage_Weapon_Min_Total_All] = definition.WeaponDamageMin;
                Attributes[GameAttribute.Damage_Weapon_Delta_Total_All] = definition.WeaponDamageDelta;
            }
        }

        private void ApplyArmorSpecificOptions(ItemTable definition)
        {
            if (definition.ArmorValue > 0)
            {
                Attributes[GameAttribute.Armor_Item] += definition.ArmorValue;
                Attributes[GameAttribute.Armor_Item_SubTotal] += definition.ArmorValue;
                Attributes[GameAttribute.Armor_Item_Total] += definition.ArmorValue;
            }
        }

        private void ApplyDurability(ItemTable definition)
        {
            if (definition.DurabilityMin > 0)
            {
                int durability = definition.DurabilityMin + RandomHelper.Next(definition.DurabilityDelta);
                Attributes[GameAttribute.Durability_Cur] = durability;
                Attributes[GameAttribute.Durability_Max] = durability;
            }
        }

        private void ApplySkills(ItemTable definition)
        {
            if (definition.SNOSkill0 != -1)
            {
                Attributes[GameAttribute.Skill, definition.SNOSkill0] = 1;
            }
            if (definition.SNOSkill1 != -1)
            {
                Attributes[GameAttribute.Skill, definition.SNOSkill1] = 1;
            }
            if (definition.SNOSkill2 != -1)
            {
                Attributes[GameAttribute.Skill, definition.SNOSkill2] = 1;
            }
            if (definition.SNOSkill3 != -1)
            {
                Attributes[GameAttribute.Skill, definition.SNOSkill3] = 1;
            }
        }

        private void ApplyAttributeSpecifier(ItemTable definition)
        {
            foreach (var effect in definition.Attribute)
            {
                float result;
                if (FormulaScript.Evaluate(effect.Formula.ToArray(), this.RandomGenerator, out result))
                {
                    var attr = GameAttribute.GameAttributeArray[effect.AttributeId] as GameAttributeF;
                    if (attr != null)
                    {
                        Logger.Debug("Randomized value for attribute " + attr.Name + " is " + result);
                        if (effect.SNOParam != -1)
                            Attributes[attr, effect.SNOParam] += result;
                        else
                            Attributes[attr] += result;
                    }
                }
            }
        }

        // There are 2 VisualItemClasses... any way to use the builder to create a D3 Message?
        public VisualItem CreateVisualItem()
        {
            return new VisualItem()
            {
                GbId = this.GBHandle.GBID,
                Field1 = Attributes[GameAttribute.DyeType],
                Field2 = 0,
                Field3 = -1
            };
        }

        #region Is*
        public static bool IsHealthGlobe(ItemTypeTable itemType)
        {
            return ItemGroup.IsSubType(itemType, "HealthGlyph");
        }

        public static bool IsGold(ItemTypeTable itemType)
        {
            return ItemGroup.IsSubType(itemType, "Gold");
        }

        public static bool IsPotion(ItemTypeTable itemType)
        {
            return ItemGroup.IsSubType(itemType, "Potion");
        }

        public static bool IsAccessory(ItemTypeTable itemType)
        {
            return ItemGroup.IsSubType(itemType, "Jewelry");
        }

        public static bool IsRuneOrJewel(ItemTypeTable itemType)
        {
            return ItemGroup.IsSubType(itemType, "Gem") ||
                ItemGroup.IsSubType(itemType, "SpellRune");
        }

        public static bool IsJournalOrScroll(ItemTypeTable itemType)
        {
            return ItemGroup.IsSubType(itemType, "Scroll") ||
                ItemGroup.IsSubType(itemType, "Book");
        }

        public static bool IsDye(ItemTypeTable itemType)
        {
            return ItemGroup.IsSubType(itemType, "Dye");
        }

        public static bool IsWeapon(ItemTypeTable itemType)
        {
            return ItemGroup.IsSubType(itemType, "Weapon");
        }

        public static bool IsArmor(ItemTypeTable itemType)
        {
            return ItemGroup.IsSubType(itemType, "Armor");
        }

        public static bool Is2H(ItemTypeTable itemType)
        {
            return ItemGroup.Is2H(itemType);
        }
        #endregion

        public void SetInventoryLocation(int equipmentSlot, int column, int row)
        {
            this.EquipmentSlot = equipmentSlot;
            this.InventoryLocation.X = column;
            this.InventoryLocation.Y = row;
            if (this.Owner != null)
                this.Owner.InGameClient.SendMessage(this.ACDInventoryPositionMessage);
        }

        public void Drop(Mooege.Core.GS.Player.Player owner, Vector3D position)
        {
            this.Owner = owner;
            this.Position = position;
            // TODO: Notify the world so that players get the state change
        }

        public override void OnTargeted(Mooege.Core.GS.Player.Player player, TargetMessage message)
        {
            //Logger.Trace("OnTargeted");
            player.Inventory.PickUp(this);
        }

        public override bool Reveal(Mooege.Core.GS.Player.Player player)
        {
            if (!base.Reveal(player))
                return false;

            // Drop effect/sound? TODO find out
            player.InGameClient.SendMessage(new PlayEffectMessage()
            {
                ActorId = this.DynamicID,
                Effect = Effect.SecondaryRessourceEffect
            });

            var affixGbis = new int[AffixList.Count];
            for (int i = 0; i < AffixList.Count; i++)
            {
                affixGbis[i] = AffixList[i].AffixGbid;
            }

            player.InGameClient.SendMessage(new AffixMessage()
            {
                ActorID = DynamicID,
                Field1 = 0x00000001,
                aAffixGBIDs = affixGbis,
            });

            player.InGameClient.SendMessage(new AffixMessage()
            {
                ActorID = DynamicID,
                Field1 = 0x00000002,
                aAffixGBIDs = new int[0],
            });

            return true;
        }
    }
}
